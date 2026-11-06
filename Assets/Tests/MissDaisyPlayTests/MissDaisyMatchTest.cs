// Plays 341 Miss Daisy like a driver in the Reefscape scene and checks real points, not just piece movement:
// floor coral driven into, HP station, coral on L1-L4 via auto-align, reef algae driven into (low and high),
// floor algae backed into, processor and barge, deep cage climb. Logs "[MM]" lines; every check ends in a
// "RESULT <name> PASS|FAIL ..." line.
//   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter MissDaisyMatchTest -testResults r.xml -logFile l.log
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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

// Physics step by step: the biggest jump of a coral's position and of its long axis while the intake or claw has it
// (teleports, snaps). Spin about its own axis does not show on a tube and is left out.
public class MissDaisyCoralMotion : MonoBehaviour
{
    public float MaxStep, MaxTurn;
    public System.Func<string> Phase, Detail;
    public string Label = "";
    public Transform Frame;
    Vector3 _p, _a, _axis;
    bool _has;
    void Start()
    {
        var mf = GetComponentInChildren<MeshFilter>();
        var ext = mf ? Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale) : Vector3.one;
        var axis = ext.x >= ext.y && ext.x >= ext.z ? Vector3.right : ext.y >= ext.z ? Vector3.up : Vector3.forward;
        _axis = mf ? transform.InverseTransformDirection(mf.transform.TransformDirection(axis)) : axis;
    }
    void FixedUpdate()
    {
        var pos = Frame ? Frame.InverseTransformPoint(transform.position) : transform.position;
        var a = transform.rotation * _axis;
        if (Frame) a = Frame.InverseTransformDirection(a);
        var step = _has ? (pos - _p).magnitude : 0f;
        var turn = _has ? Mathf.Min(Vector3.Angle(a, _a), Vector3.Angle(a, -_a)) : 0f;
        _has = true;
        _p = pos;
        _a = a;
        var ph = Phase != null ? Phase() : "";
        if (ph.Length == 0) return;
        MaxStep = Mathf.Max(MaxStep, step);
        MaxTurn = Mathf.Max(MaxTurn, turn);
        if (step > 0.05f || turn > 7f) Debug.Log($"[MM] {Label} jump: t={Time.fixedTime:F3} {step * 100f:F1} cm, {turn:F1} deg in a physics step {ph} now at {pos:F3} parent={(transform.parent ? transform.parent.name : "-")} {(Detail != null ? Detail() : "")}");
    }
}

public class MissDaisyMatchTest
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string Meta = "Assets/Prefabs/Reefscape/Robots/Mods/MissDaisy/341/MissDaisy.asset";
    ReefscapeRobotBase _robot;
    const float BargeStandOff = 1.0f; // robot centre to the net's middle line (m) for the fling
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

    // Robot meshes with vertices inside a held coral (a 0.30 m x 0.057 m capsule), the compliant wheels excepted.
    static readonly string[] Squishy = { "am-3949", "am-3945", "1.625\"", "Coral Intake Front Tube" };
    List<string> Penetrating(Transform coral)
    {
        var mf = coral.GetComponentInChildren<MeshFilter>();
        var size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
        var local = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
        var axis = mf.transform.TransformDirection(local).normalized;
        var c = mf.transform.TransformPoint(mf.sharedMesh.bounds.center);
        Vector3 a = c - axis * 0.13f, b = c + axis * 0.13f;
        var hits = new List<string>();
        foreach (var r in _robot.GetComponentsInChildren<MeshFilter>())
        {
            if (Squishy.Any(n => r.name.StartsWith(n)) || r.GetComponentInParent<PieceCtrl>() || !r.sharedMesh) continue;
            var rb = r.GetComponent<Renderer>();
            if (rb && !rb.bounds.Intersects(new Bounds(c, Vector3.one * 0.36f))) continue;
            var m = r.transform.localToWorldMatrix;
            foreach (var v in r.sharedMesh.vertices)
            {
                var w = m.MultiplyPoint3x4(v);
                var t = Mathf.Clamp01(Vector3.Dot(w - a, b - a) / (b - a).sqrMagnitude);
                if ((w - (a + (b - a) * t)).magnitude < 0.057f - 0.008f) { hits.Add(r.name); break; }
            }
        }
        return hits;
    }

    // MD_CAPTURE=<dir>: side-on (and three-quarter) frames of the robot, to look at poses (needs a run with graphics).
    static readonly string CaptureDir = Environment.GetEnvironmentVariable("MD_CAPTURE");
    void Capture(string name, Vector3 lookAt, Vector3 faceDir)
    {
        if (string.IsNullOrEmpty(CaptureDir)) return;
        Directory.CreateDirectory(CaptureDir);
        var side = Vector3.Cross(Vector3.up, faceDir).normalized;
        foreach (var (tag, from) in new[] { ("side", lookAt + side * 1.7f + Vector3.up * 0.15f),
                                            ("3q", lookAt - faceDir * 1.3f + side * 1.1f + Vector3.up * 0.6f) })
        {
            var go = new GameObject("Capture Cam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 45;
            cam.nearClipPlane = 0.05f;
            go.transform.position = from;
            go.transform.LookAt(lookAt);
            var rt = new RenderTexture(960, 640, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(960, 640, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 960, 640), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(CaptureDir, $"{name}-{tag}.png"), tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.Destroy(rt);
            Object.Destroy(tex);
            Object.Destroy(go);
        }
    }

    // A tutorial card picture: the robot (or what it is doing) in the middle, from further out, with the coral lying
    // about the carpet hidden for the shot.
    void CardShot(string name, Vector3 lookAt, Vector3 faceDir)
    {
        if (string.IsNullOrEmpty(CaptureDir)) return;
        Directory.CreateDirectory(CaptureDir);
        var hidden = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == "Coral" && p.transform.position.y < 0.25f && !p.transform.IsChildOf(_robot.transform))
            .SelectMany(p => p.GetComponentsInChildren<Renderer>()).Where(r => r.enabled).ToList();
        foreach (var r in hidden) r.enabled = false;
        var side = Vector3.Cross(Vector3.up, faceDir).normalized;
        var go = new GameObject("Card Cam");
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = 40;
        cam.nearClipPlane = 0.05f;
        go.transform.position = lookAt - faceDir * 2.6f + side * 1.9f + Vector3.up * 1.1f;
        go.transform.LookAt(lookAt);
        var rt = new RenderTexture(900, 800, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(900, 800, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 900, 800), 0, 0);
        tex.Apply();
        File.WriteAllBytes(Path.Combine(CaptureDir, $"card-{name}.png"), tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.Destroy(rt);
        Object.Destroy(tex);
        Object.Destroy(go);
        foreach (var r in hidden) if (r) r.enabled = true;
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
        // a copy: the handler refills the same object every LateUpdate
        var live = (_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? BlueData : RedData).GetValue(h);
        return (ReefscapeScoreData)typeof(object).GetMethod("MemberwiseClone", F).Invoke(live, null);
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
                        (name != "Coral" || p.transform.position.y < 0.2f) && // not one scored on the reef
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
        if (mf) axis = p.transform.InverseTransformDirection(mf.transform.TransformDirection(axis)); // in the piece's own frame
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
        var keys = DriveKeys(worldDir);
        Hold(keys);
        yield return new WaitForSeconds(seconds);
        Release(keys);
        yield return new WaitForSeconds(0.4f);
    }

    Key[] DriveKeys(Vector3 worldDir)
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
        return keys.ToArray();
    }

    IEnumerator Reset(Games.Reefscape.Enums.ReefscapeRobotMode mode, DropType drop)
    {
        Keys();
        if (HasCoral || HasAlgae)
        {
            yield return Tap(Key.K);
            yield return new WaitForSeconds(1.5f);
            // a coral let go at stow sits in the claw: off to the side for the next test
            var claw = Find("Coral Claw State").position;
            foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral" && (p.transform.position - claw).magnitude < 0.4f && !_robot.GetComponentsInChildren<PieceCtrl>().Contains(p)))
                Put(p, Open + new Vector3(1.5f, 0.06f, 2f), p.transform.rotation);
        }
        if (_robot.CurrentRobotMode != mode) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != drop) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1.5f);
    }

    Vector3 Open => new Vector3(-2.5f, 0, 1.6f); // open carpet on the blue side, away from the reef

    // ---------------------------------------------------------------- pieces into the robot

    // Floor coral: lying across the robot's path 1.1 m ahead, the robot drives into it holding intake.
    int _pickupShots;
    bool _hpShot;

    // A coral the intake has started on is knocked out of line (here: moved 0.25 m to the side, as when the driver
    // turns off it): it must be let go where it lies, not flown across to the claw.
    IEnumerator FloorCoralKnockedAway(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        Put(coral, t.position - t.forward * 1.1f + Vector3.up * 0.06f, Along(coral, t.right));
        yield return new WaitForSeconds(0.5f);
        var groundIntake = Find("Ground Coral Intake").GetComponent<RobotFramework.Components.GamePieceIntake<Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>>();
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        var end = Time.time + 4f;
        // knocked the moment the intake has it (between drive pulses it had already reached the claw)
        var driveKeys = DriveKeys(-t.forward);
        Hold(driveKeys);
        while (Time.time < end && !(groundIntake.hasGamePiece && groundIntake.GamePiece == coral.gameObject)) yield return new WaitForFixedUpdate();
        Release(driveKeys);
        var body = coral.GetComponentInChildren<Rigidbody>();
        var knockedTo = coral.transform.position + t.right * 0.6f; // clear of the rollers, which pull whatever they touch
        body.position = knockedTo;
        coral.transform.position = knockedTo;
        body.velocity = Vector3.zero;
        var maxSpeed = 0f;
        for (var k = 0; k < 75; k++)
        {
            yield return new WaitForFixedUpdate();
            maxSpeed = Mathf.Max(maxSpeed, body.velocity.magnitude);
        }
        var moved = (coral.transform.position - knockedTo).magnitude;
        Result(label, !HasCoral && maxSpeed < 1f && moved < 0.2f,
            $"{Status()} wasDetected={!float.IsInfinity(end)} inClaw={HasCoral} maxCoralSpeed={maxSpeed:F2}m/s movedAfterKnock={moved:F2}m");
        // still holding intake, the driver lines up on it again: it must be taken (no giving up on a coral that got away)
        yield return new WaitForSeconds(1.5f);
        Put(coral, t.position - t.forward * 1.1f + Vector3.up * 0.06f, Along(coral, t.right));
        yield return new WaitForSeconds(0.5f);
        end = Time.time + 8f;
        while (Time.time < end && !(HasCoral && NodeOf("Coral").atTarget))
        {
            yield return Drive(-t.forward, 0.1f);
            L($"{label} retry: coral(robot)={Local(coral.transform.position)} intakeHas={groundIntake.hasGamePiece} v={body.velocity:F2} spin={body.angularVelocity:F1} carrot={Local(Find("Coral Carrot").position)} robot={t.position:F2} other={(groundIntake.GamePiece ? groundIntake.GamePiece.name : "-")}");
        }
        Release(Key.LeftShift);
        Result(label + ", retried without re-pressing intake", HasCoral, Status());
    }

    // Holding one piece, the driver switches mode and holds intake: the arm must stay stowed (no reaching for the other
    // piece with the claw full), then the mode goes back.
    IEnumerator OtherPieceIntake(string label)
    {
        var mode = _robot.CurrentRobotMode;
        yield return Tap(Key.Semicolon);
        Hold(Key.LeftShift);
        var maxShoulder = 0f;
        var maxIntake = 0f;
        for (var k = 0; k < 75; k++)
        {
            yield return new WaitForFixedUpdate();
            maxShoulder = Mathf.Max(maxShoulder, Mathf.Abs(Mathf.DeltaAngle(0, (Field("shoulder") as GenericJoint).GetSingleAxisAngle(JointAxis.X))));
            maxIntake = Mathf.Max(maxIntake, Mathf.Abs(Mathf.DeltaAngle(0, (Field("intakePivot") as GenericJoint).GetSingleAxisAngle(JointAxis.X))));
        }
        var status = Status();
        Release(Key.LeftShift);
        var both = HasCoral && HasAlgae;
        if (_robot.CurrentRobotMode != mode) yield return Tap(Key.Semicolon);
        yield return new WaitForSeconds(0.5f);
        Result(label, maxShoulder < 10f && maxIntake < 10f && !both,
            $"{status} maxShoulder={maxShoulder:F0} maxIntakePivot={maxIntake:F0} holdingBoth={both}");
    }

    // Place pressed at stow: the coral leaves the claw and lands on or beside the robot, never passing through its
    // frame (below the frame top while over the frame's footprint).
    IEnumerator OuttakeInStow(string label)
    {
        var coral = NodeOf("Coral").controller.GetComponentInParent<PieceCtrl>();
        var body = coral.GetComponentInChildren<Rigidbody>();
        Hold(Key.K);
        var through = false;
        var path = new List<string>();
        var fastest = 0f;
        var start = _robot.transform.InverseTransformPoint(body.worldCenterOfMass);
        for (var k = 0; k < 300; k++)
        {
            yield return new WaitForFixedUpdate();
            var l = _robot.transform.InverseTransformPoint(body.worldCenterOfMass);
            if (!HasCoral) fastest = Mathf.Max(fastest, new Vector3(body.velocity.x, 0f, body.velocity.z).magnitude);
            // inside the frame rails (+-0.33) and below the top covers (0.17) less a coral radius: it went through
            if (Mathf.Abs(l.x) < 0.30f && Mathf.Abs(l.z) < 0.30f && l.y < 0.17f) through = true;
            if (k % 25 == 0) path.Add($"{l:F2}v{_robot.transform.InverseTransformDirection(body.velocity):F2}{(body.isKinematic ? "K" : "")}{(body.useGravity ? "" : "noG")}");
            if (k == 20) Release(Key.K);
        }
        // (a second later it may still be settling: no push means no sideways speed to speak of)
        // out of the claw and clear of everything: not left inside it, not inside anything else
        var claw = Find("Coral Claw State").position;
        var clear = (coral.transform.position - claw).magnitude;
        var inside = Penetrating(coral.transform).ToList();
        Result(label, !HasCoral && !through && fastest < 1.5f && clear > 0.08f && inside.Count == 0, $"{Status()} throughFrame={through} fastestSideways={fastest:F2}m/s fromClaw={clear:F2}m inside=[{string.Join(",", inside)}] now {Local(coral.transform.position)} coral(robot) {string.Join(" ", path)}");
    }

    // How the arm settles (the fore-aft wobble): stow -> L4 -> stow -> L2, the shoulder angle and the chassis pitch every
    // physics step. Overshoot past the final angle and the swing left in the last 0.4 s; informational.
    IEnumerator ArmSettle()
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(1f);
        foreach (var (name, key) in new[] { ("L4", Key.P), ("stow", Key.Z), ("L2", Key.I), ("stow again", Key.Z) })
        {
            Hold(key);
            var sh = new List<float>();
            var pitch = new List<float>();
            for (var k = 0; k < 125; k++)
            {
                yield return new WaitForFixedUpdate();
                if (k == 8) Release(key);
                sh.Add(Mathf.DeltaAngle(0, (Field("shoulder") as GenericJoint).GetSingleAxisAngle(JointAxis.X)));
                pitch.Add(Vector3.SignedAngle(Vector3.up, _robot.transform.up, _robot.transform.right));
            }
            var final = sh[^1];
            var start = sh[0];
            var dir = Mathf.Sign(final - start);
            var overshoot = sh.Max(a => (a - final) * dir);
            var tail = sh.Skip(sh.Count - 20).ToList();
            var moveSteps = sh.FindIndex(a => Mathf.Abs(a - final) < 1f);
            Result($"arm settle to {name}", true,
                $"from {start:F1} to {final:F1} reached(1deg) after {moveSteps * Time.fixedDeltaTime:F2}s overshoot={overshoot:F1}deg " +
                $"lastSwing={tail.Max() - tail.Min():F2}deg chassisPitch {pitch.Min():F2}..{pitch.Max():F2}deg trace " +
                string.Join(",", sh.Where((_, i) => i % 5 == 0).Select(a => a.ToString("F0"))));
        }
    }

    // Drives straight and does not turn: each way across the carpet, the yaw and the sideways drift.
    IEnumerator DriveCheck()
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        var dirs = new[] { ("+x", Vector3.right), ("-x", Vector3.left), ("+z", Vector3.forward), ("-z", Vector3.back) };
        foreach (var (name, dir) in dirs)
        {
            PlaceRobot(Open, Vector3.forward);
            yield return new WaitForSeconds(0.7f);
            var start = _robot.transform.position;
            var yaw0 = _robot.transform.eulerAngles.y;
            yield return Drive(dir, 0.5f); // (short: a longer run into +z meets the field)
            yield return new WaitForSeconds(0.5f);
            var moved = _robot.transform.position - start;
            var side = Vector3.ProjectOnPlane(moved, dir);
            side.y = 0;
            var yaw = Mathf.DeltaAngle(yaw0, _robot.transform.eulerAngles.y);
            Result($"drive {name} straight", Mathf.Abs(yaw) < 3f && side.magnitude < 0.1f * Mathf.Max(moved.magnitude, 0.5f),
                $"travel={Vector3.Dot(moved, dir):F2}m sideways={side.magnitude:F3}m yaw={yaw:F1}deg");
        }
    }

    // A coral the rollers have not reached is left alone: no force reaches out to it.
    IEnumerator ReleaseBeforeRising(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        Put(coral, t.position - t.forward * 0.86f + Vector3.up * 0.06f, Along(coral, t.right));
        yield return new WaitForSeconds(0.5f);
        var at = coral.transform.position;
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(3f);
        var moved = (coral.transform.position - at).magnitude;
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, moved < 0.03f && !HasCoral, $"{Status()} movedWithoutTouching={moved:F3}m inClaw={HasCoral}");
    }

    // Two coral side by side in the intake's box: the rollers pull both, not only the one the intake picked.
    IEnumerator TwoCoral(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var a = FreePiece("Coral", t.position);
        Put(a, t.position - t.forward * 0.78f + t.right * 0.16f + Vector3.up * 0.06f, Along(a, t.forward));
        var b = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).First(p => p.PieceName == "Coral" && p != a && p.transform.position.y < 0.2f);
        Put(b, t.position - t.forward * 0.78f - t.right * 0.16f + Vector3.up * 0.06f, Along(b, t.forward));
        yield return new WaitForSeconds(0.3f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        // both drawn past the front roller (z ~ -0.6) or up inside, not shoved along the carpet in front of it, until the
        // first reaches the claw (then the intake stows and the other is left)
        var movedA = float.NegativeInfinity;
        var movedB = float.NegativeInfinity;
        var end = Time.time + 8f;
        var keys = DriveKeys(-t.forward);
        while (Time.time < end && !HasCoral)
        {
            var n = Mathf.FloorToInt((Time.time - end) * 2f); // half-second pulses
            if ((Time.time - end) * 2f - n < 0.2f) Hold(keys);
            else Release(keys);
            movedA = Mathf.Max(movedA, t.InverseTransformPoint(a.transform.position).z + 0.6f);
            movedB = Mathf.Max(movedB, t.InverseTransformPoint(b.transform.position).z + 0.6f);
            yield return new WaitForFixedUpdate();
        }
        Release(keys);
        yield return new WaitForSeconds(2f);
        var claw = HasCoral;
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, movedA > 0f && movedB > 0f, $"past the roller by a={movedA:F2}m b={movedB:F2}m oneInClaw={claw}");
    }

    // still: the robot stops 0.2 s after the intake first has the coral (a driver letting the rollers do the rest)
    IEnumerator GetFloorCoral(string label, float lateral = 0f, float yaw = 0f, bool still = false)
    {
        _pickupShots = 0;
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        Put(coral, t.position - t.forward * 1.1f + t.right * lateral + Vector3.up * 0.06f,
            Along(coral, Quaternion.AngleAxis(yaw, Vector3.up) * t.right)); // yaw 0: across the intake
        yield return new WaitForSeconds(0.5f);
        var start = t.position;
        var coralLog = coral.gameObject.AddComponent<MissDaisyContactLog>();
        var motionIntake = Find("Ground Coral Intake").GetComponent<RobotFramework.Components.GamePieceIntake<Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>>();
        var motion = coral.gameObject.AddComponent<MissDaisyCoralMotion>();
        motion.Label = label;
        motion.Frame = t;
        motion.Detail = () => $"claw {Local(Find("Coral Claw State").position)} {Status().Replace(" mode=Coral drop=Ground", "").Replace(" algae=False", "")}";
        // the catch too, but not after: held, a coral goes with the claw (it re-attaches per rendered frame, so a stalled frame on this headless machine reads as a jump)
        var caughtSteps = 0;
        motion.Phase = () => motionIntake.hasGamePiece && motionIntake.GamePiece == coral.gameObject ? "intake" : HasCoral && caughtSteps++ < 1 ? "catch" : "";
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        var drift = Vector3.ProjectOnPlane(t.position - start, Vector3.up).magnitude;
        var end = Time.time + 6f;
        var maxSpeed = 0f;
        var last = coral.transform.position;
        var lastT = Time.time;
        var flap = Find("Intake Flap");
        var flapMax = 0f;
        var clipped = new HashSet<string>();
        var armMovedEarly = false;
        var k = 0;
        var groundIntake = Find("Ground Coral Intake").GetComponent<RobotFramework.Components.GamePieceIntake<Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>>();
        var firstHold = float.PositiveInfinity;
        while (Time.time < end && !(HasCoral && NodeOf("Coral").atTarget && NodeOf("Coral").GetCurrentState()?.name == "Coral Claw"))
        {
            if (groundIntake.hasGamePiece && groundIntake.GamePiece == coral.gameObject && float.IsPositiveInfinity(firstHold)) firstHold = Time.time;
            if (k % 4 == 0 && !HasCoral)
            {
                var fr = Find("Front Roller");
                var frb = fr.GetComponent<Rigidbody>();
                var crb = coral.GetComponentInChildren<Rigidbody>();
                L($"{label} roll: coral(robot)={t.InverseTransformPoint(coral.transform.position):F3} coralVel={t.InverseTransformDirection(crb.velocity):F2} coralSpin={t.InverseTransformDirection(crb.angularVelocity):F1} " +
                  $"roller(robot)={t.InverseTransformPoint(fr.position):F3} rollerSpinX={t.InverseTransformDirection(frb.angularVelocity).x - t.InverseTransformDirection(_robot.GetComponent<Rigidbody>().angularVelocity).x:F1} intakeHas={groundIntake.hasGamePiece}");
            }
            if (!HasCoral && !(still && Time.time - firstHold > 0.2f)) yield return Drive(-t.forward, 0.1f);
            else yield return new WaitForFixedUpdate();
            var v = (coral.transform.position - last).magnitude / Mathf.Max(Time.time - lastT, 1e-4f);
            last = coral.transform.position;
            lastT = Time.time;
            if (HasCoral) maxSpeed = Mathf.Max(maxSpeed, v);
            if (flap) flapMax = Mathf.Max(flapMax, Quaternion.Angle(Quaternion.identity, flap.localRotation));
            if (HasCoral && !(NodeOf("Coral").atTarget && NodeOf("Coral").GetCurrentState()?.name == "Coral Claw") &&
                Mathf.Abs(Mathf.DeltaAngle((Field("shoulder") as GenericJoint).GetSingleAxisAngle(JointAxis.X), -115f)) > 10f)
                armMovedEarly = true;
            if (k % 25 == 0)
                L($"{label} path: carrot={Local(Find("Coral Carrot").position)} claw={Local(Find("Coral Claw State").position)} " +
                  $"p1={Local(Find("Coral Path 1 Front Roller").position)} p2={Local(Find("Coral Path 2 Top Roller").position)} " +
                  $"clawOut={_robot.transform.InverseTransformDirection(Find("Claw Eject").forward):F2} coral={Local(coral.transform.position)}");
            if (k % 25 == 0 && !HasCoral)
                L($"{label} approach: {Status()} coral(robot)={Local(coral.transform.position)} intakeBox(robot)={Local(Find("Ground Coral Intake").position)} exit(robot)={Local(Find("Coral Intake Exit").position)} flap={(flap ? Quaternion.Angle(Quaternion.identity, flap.localRotation) : -1):F0}");
            if (HasCoral && k % 5 == 0)
                foreach (var hit in Penetrating(coral.transform)) clipped.Add(hit);
            // the coral against the flap: along its belts from the pivot, and off the belt line (negative = into them)
            var flapT = Find("Intake Flap");
            var beltsT = Find("Flap Belts");
            if (flapT && beltsT && k == 1)
                L($"{label} flap path: " + string.Join(" ", new[] { "Coral Path 1 Front Roller", "Coral Path 1b Belts", "Coral Path 2 Top Roller", "Coral Path 3 Past Flap", "Coral Claw State" }
                    .Select(n => n.Split(' ').Last() + "=" + (Find(n) ? beltsT.InverseTransformPoint(Find(n).position).ToString("F3") : "-"))));
            if (flapT && beltsT && !HasCoral && k % 3 == 0)
            {
                var b = beltsT.InverseTransformPoint(coral.transform.position); // belts box frame: z along, y away from the coral side
                var armsT = Find("Roller Arms");
                L($"{label} flap: angle={Quaternion.Angle(Quaternion.identity, flapT.localRotation):F1} flapEuler={flapT.localEulerAngles:F1} " +
                  $"arms={(armsT ? armsT.localEulerAngles.ToString("F1") : "-")} armsVsIntake={(armsT ? Quaternion.Angle(armsT.parent.rotation, armsT.rotation).ToString("F1") : "-")} coralInBelts(x,y,z)={b:F3} " +
                  $"beltHalfLen={(beltsT.GetComponent<BoxCollider>() ? beltsT.GetComponent<BoxCollider>().size.z / 2 : 0f):F3}");
            }
            // the coral's way through the intake, for the eye (graphics runs only)
            if (_pickupShots < 6 && NodeOf("Coral") != null && Find("Ground Coral Intake").GetComponent<RobotFramework.Components.GamePieceIntake<Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>>().hasGamePiece && k % 2 == 0)
                Capture($"{label.Replace(' ', '_')}-pickup-{_pickupShots++}", coral.transform.position, _robot.transform.right);
            if (HasCoral && v > 2f) // fast frames of the transfer
                L($"{label} transfer f{k}: state={NodeOf("Coral").GetCurrentState()?.name} atTarget={NodeOf("Coral").atTarget} coral(robot)={Local(coral.transform.position)} parent={(coral.transform.parent ? coral.transform.parent.name : "-")} v={v:F2}");
            if (k++ % 10 == 0 && HasCoral)
                L($"{label} path: state={NodeOf("Coral").GetCurrentState()?.name} atTarget={NodeOf("Coral").atTarget} coral(robot)={Local(coral.transform.position)} flap={(flap ? Quaternion.Angle(Quaternion.identity, flap.localRotation) : -1):F0}");
        }
        Release(Key.LeftShift);
        Object.Destroy(coralLog);
        motion.Phase = null;
        Object.Destroy(motion);
        yield return new WaitForSeconds(1.5f);
        var inClaw = HasCoral && NodeOf("Coral").atTarget && NodeOf("Coral").GetCurrentState()?.name == "Coral Claw";
        foreach (var hit in Penetrating(coral.transform)) clipped.Add(hit); // held at stow
        Result(label, inClaw && drift < 0.03f && maxSpeed < 3f && !armMovedEarly && flapMax > 5f && clipped.Count == 0 && motion.MaxStep < 0.05f && motion.MaxTurn < 7f,
            $"{Status()} coral={Local(coral.transform.position)} robotDriftWhileIntaking={drift:F3}m maxCoralSpeed={maxSpeed:F2}m/s flapLift={flapMax:F0}deg armMovedEarly={armMovedEarly} clipsThrough=[{string.Join(",", clipped)}] biggestStep={motion.MaxStep * 100f:F1}cm {motion.MaxTurn:F1}deg");
    }

    // Driven up to a floor coral only until the front roller touches it, then the robot stops: the rollers alone must take it.
    IEnumerator RollerTakesIt(string label, float lateral, float yaw)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        Put(coral, t.position - t.forward * 1.1f + t.right * lateral + Vector3.up * 0.06f, Along(coral, Quaternion.AngleAxis(yaw, Vector3.up) * t.right));
        yield return new WaitForSeconds(0.5f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        var keys = DriveKeys(-t.forward);
        var fr = Find("Front Roller").GetComponent<Collider>();
        var coralCols = coral.GetComponentsInChildren<Collider>();
        bool Touching()
        {
            foreach (var c in coralCols)
                if (Physics.ComputePenetration(fr, fr.transform.position, fr.transform.rotation, c, c.transform.position, c.transform.rotation, out _, out var d) && d > 0f) return true;
            return false;
        }
        var end = Time.time + 6f;
        Hold(keys);
        while (Time.time < end && !Touching()) yield return new WaitForFixedUpdate();
        Release(keys);
        var touched = Time.time;
        var groundIntake = Find("Ground Coral Intake").GetComponent<RobotFramework.Components.GamePieceIntake<Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>>();
        var taken = float.PositiveInfinity;
        end = Time.time + 5f;
        L($"{label}: touched at coral(robot)={Local(coral.transform.position)}");
        var fk = 0;
        while (Time.time < end && !HasCoral)
        {
            if (float.IsPositiveInfinity(taken) && groundIntake.hasGamePiece && groundIntake.GamePiece == coral.gameObject) taken = Time.time - touched;
            if (fk++ % 10 == 0) L($"{label}: +{Time.time - touched:F2}s coral(robot)={Local(coral.transform.position)} v={t.InverseTransformDirection(coral.GetComponentInChildren<Rigidbody>().velocity):F2} roller(robot)={Local(fr.transform.position)}");
            yield return new WaitForFixedUpdate();
        }
        L($"{label}: taken after {taken:F2}s");
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, HasCoral && taken < 1.5f, $"intake had it {taken:F2}s after the roller touched it, robot stopped");
    }

    // A coral that has got in under the flaps by any route: the intake takes it up to the claw from there.
    IEnumerator UnderFlap(string label, Vector3 local, float yaw)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        Put(coral, t.TransformPoint(local), Along(coral, Quaternion.AngleAxis(yaw, t.up) * t.right));
        var start = Time.time;
        while (Time.time - start < 5f && !HasCoral) yield return new WaitForFixedUpdate();
        var took = Time.time - start;
        yield return new WaitForSeconds(0.2f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, HasCoral && took < 2f, $"in the claw after {took:F2}s {Status()}");
    }

    // At L4 with a coral, a hard stop from full speed: how far the robot pitches and the claw swings. With the low centre of
    // mass and raised solver iterations, then without.
    IEnumerator L4Wobble()
    {
        yield return GetFloorCoral("floor coral for the L4 wobble check");
        if (!HasCoral) yield break;
        yield return Tap(Key.P);
        yield return new WaitForSeconds(3f);
        var body = _robot.GetComponent<Rigidbody>();
        var bodies = _robot.GetComponentsInChildren<Rigidbody>();
        foreach (var label in new[] { "low COM", "COM as it was" })
        {
            if (label == "COM as it was") body.automaticCenterOfMass = true;
            foreach (var (dirName, dir) in new[] { ("forward", Vector3.forward), ("sideways", Vector3.right) })
            {
                PlaceRobot(Open, Vector3.forward);
                yield return new WaitForSeconds(1.5f);
                var t = _robot.transform;
                var claw = Find("Coral Claw State");
                var rest = t.InverseTransformPoint(claw.position);
                var keys = DriveKeys(dir);
                Hold(keys);
                yield return new WaitForSeconds(0.5f); // up to speed, well short of anything to hit
                Release(keys);
                float pitch = 0f, swing = 0f, settled = 0f;
                for (var k = 0; k < 450; k++)
                {
                    yield return new WaitForFixedUpdate();
                    pitch = Mathf.Max(pitch, Vector3.Angle(t.up, Vector3.up));
                    var d = (t.InverseTransformPoint(claw.position) - rest).magnitude;
                    swing = Mathf.Max(swing, d);
                    if (d > 0.01f || Vector3.Angle(t.up, Vector3.up) > 0.3f) settled = k * Time.fixedDeltaTime;
                }
                L($"L4 wobble {label} {dirName}: chassis tilt {pitch:F2}deg claw swing {swing * 100f:F1}cm settled after {settled:F2}s");
                if (label == "low COM") Result($"L4 hard stop {dirName} stays steady", pitch < 3f && swing < 0.08f, $"tilt {pitch:F2}deg swing {swing * 100f:F1}cm settled {settled:F2}s");
            }
        }
        yield return Tap(Key.P);
        yield return new WaitForSeconds(1.5f);
        body.automaticCenterOfMass = false;
        body.centerOfMass = new Vector3(body.centerOfMass.x, 0.03f, body.centerOfMass.z);
    }

    // L1 from stow out in the open: it gets to the setpoint, however long it takes, and how the claw and coral sit over the
    // trough lip (scored separately at the reef).
    IEnumerator L1FromStow()
    {
        yield return GetFloorCoral("floor coral for L1 from stow");
        if (!HasCoral) yield break;
        var t0 = Time.time;
        yield return Tap(Key.U);
        var sh = (GenericJoint)Field("shoulder");
        var reached = float.PositiveInfinity;
        for (var k = 0; k < 150 && float.IsPositiveInfinity(reached); k++)
        {
            yield return new WaitForFixedUpdate();
            var el = (Field("elevator") as GenericElevator).GetElevatorHeight();
            if (Mathf.Abs(Mathf.DeltaAngle(sh.GetSingleAxisAngle(JointAxis.X), 85f)) < 2f && Mathf.Abs(el - 9.2f) < 0.4f) reached = Time.time - t0;
        }
        Result("L1 from stow reaches the setpoint", reached < 2f, $"after {reached:F2}s {Status()}");
        yield return Tap(Key.U);
        yield return new WaitForSeconds(1.5f);
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
        var rollerSpun = false;
        var end = Time.time + 8f;
        var traced = 0;
        while (Time.time < end && !HasCoral)
        {
            // how the coral comes out: its long axis in the robot's frame (y up, x across, z along)
            for (var k = 0; k < 80 && !HasCoral; k++)
            {
                yield return new WaitForFixedUpdate();
                var c = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral")
                    .OrderBy(p => (p.transform.position - spawn.position).sqrMagnitude).FirstOrDefault();
                if (c && traced++ % 4 == 0 && (c.transform.position - spawn.position).magnitude < 2f && traced < 60)
                {
                    if (!c.GetComponent<MissDaisyContactLog>()) c.gameObject.AddComponent<MissDaisyContactLog>();
                    var mesh = c.GetComponentInChildren<MeshRenderer>();
                    L($"{label} coral out: at(robot)={Local(c.transform.position)} up-tilt of {string.Join(" ", new[] { Vector3.right, Vector3.up, Vector3.forward }.Select(a => _robot.transform.InverseTransformDirection(c.transform.rotation * a).ToString("F2")))} v={c.GetComponentInChildren<Rigidbody>().velocity:F2}");
                }
            }
            if (HasCoral) break;
            var near = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral")
                .OrderBy(p => (p.transform.position - _robot.transform.position).sqrMagnitude).FirstOrDefault();
            L($"{label} waiting: {Status()} nearest coral(robot)={(near ? Local(near.transform.position) : "-")} claw intake(robot)={Local(Find("Claw Coral Intake").position)}");
            if (near && (near.transform.position - Find("Claw Coral Intake").position).magnitude < 0.6f && !_hpShot)
            {
                _hpShot = true; // the coral coming down the chute to the claw (a tutorial card shot)
                Capture("hp-intake", Find("Claw Coral Intake").position, -sf);
                CardShot("human-player", _robot.transform.position + Vector3.up * 0.8f, -sf);
            }
            rollerSpun |= _robot.GetComponentsInChildren<RobotFramework.Components.GenericRoller>().Any(r => Mathf.Abs(r.GetComponent<HingeJoint>().motor.targetVelocity) > 1f);
        }
        // the arm stays at the station a moment after the catch
        var arm = (GenericJoint)Field("shoulder");
        var before = arm.GetSingleAxisAngle(JointAxis.X);
        var caught = Time.time;
        while (Time.time - caught < 2f && Mathf.Abs(Mathf.DeltaAngle(before, arm.GetSingleAxisAngle(JointAxis.X))) < 3f) yield return new WaitForFixedUpdate();
        var moved = Time.time - caught;
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1.5f);
        Result(label, HasCoral && !rollerSpun && moved > 0.3f, $"{Status()} floorRollersSpun={rollerSpun} armWaited={moved:F2}s");
    }

    AlignNode Face(int i) => Object.FindObjectsByType<AlignNode>(FindObjectsSortMode.None)
        .Where(n => n.GetComponentInParent<Reef>() && n.GetComponentInParent<Reef>().Alliance == _robot.Alliance)
        .OrderBy(n => n.transform.position.x).ThenBy(n => n.transform.position.z).ElementAt(i);

    Vector3 ReefCentre(AlignNode face) => face.GetComponentInParent<Reef>().transform.position;

    // ---------------------------------------------------------------- scoring

    // Coral on the reef: raise 1.3 m out (facing the reef face), then hold auto-align (it drives in), then place.
    // Faces whose algae is out of the way of a level: low algae (0.9 m) sits under L3, high algae (1.3 m) over it.
    AlignNode FaceFor(string level, int fallback)
    {
        var algae = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Algae").ToList();
        var faces = Object.FindObjectsByType<AlignNode>(FindObjectsSortMode.None)
            .Where(n => n.GetComponentInParent<Reef>() && n.GetComponentInParent<Reef>().Alliance == _robot.Alliance).ToList();
        // each reef algae belongs to the face it is closest to (in plan)
        float Plan(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        var onReef = algae.Select(a => a.transform.position).Where(p => faces.Min(n => Plan(p, n.transform.position)) < 1.2f).ToList();
        float AlgaeY(AlignNode n) => onReef
            .Where(p => faces.OrderBy(m => Plan(p, m.transform.position)).First() == n)
            .Select(p => p.y).DefaultIfEmpty(-1f).Max();
        // the left branch of that level on a face, and whether a coral already sits on it
        var coral = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral")
            .Select(p => p.transform.position).ToList();
        var levelBranches = Object.FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None)
            .SelectMany(p => ((Array)Branches.GetValue(p)).Cast<object>())
            .Where(b => b.GetType().GetProperty("TargetHeight").GetValue(b).ToString() == level)
            .Select(b => (Vector3)b.GetType().GetField("center").GetValue(b)).ToList();
        Vector3 LeftBranch(AlignNode n) => levelBranches.OrderBy(b => Plan(b, n.LeftNode.transform.position)).First();
        bool Free(AlignNode n) => levelBranches.Count == 0 || coral.All(c => (c - LeftBranch(n)).magnitude > 0.25f);
        var open = (level switch
        {
            "L3" => faces.Where(n => AlgaeY(n) < 1.1f),
            "L2" => faces.Where(n => AlgaeY(n) < 0f || AlgaeY(n) > 1.1f),
            _ => faces
        }).ToList();
        var ok = open.Where(Free).ToList();
        if (ok.Count == 0 && open.Count > 0 && levelBranches.Count > 0)
        {
            // every reachable branch of this level is taken: clear one (test housekeeping, like a field reset)
            var n = open[Mathf.Min(fallback, open.Count - 1)];
            foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
                         .Where(p => p.PieceName == "Coral" && (p.transform.position - LeftBranch(n)).magnitude < 0.25f))
                p.gameObject.SetActive(false);
            return n;
        }
        return ok.Count > 0 ? ok[Mathf.Min(fallback, ok.Count - 1)] : Face(fallback);
    }

    IEnumerator ScoreCoral(string level, Key key, int face, bool overIntake = false, bool manual = false, bool early = false)
    {
        var f = FaceFor(level, face);
        var toReef = ReefCentre(f) - f.transform.position;
        toReef.y = 0;
        toReef.Normalize();
        // over the intake: facing away, auto-align turns the robot round and it scores out the back (camera flipped)
        PlaceRobot(f.transform.position - toReef * 1.3f, overIntake ? -toReef : toReef);
        var tag = (overIntake ? level + " over intake" : level) + (manual ? " (driven, no auto-align)" : "") +
                  (early ? " (place pressed on the way up)" : "");
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        var bBranch = ScoredBranches(level == "L1" ? null : level);
        var bTrough = before.CoralPoints;
        if (manual)
        {
            // line up by eye on the left branch and drive straight in until the bumper stops on the face
            PlaceRobot(f.LeftNode.transform.position - toReef * 1.0f, toReef);
            yield return new WaitForSeconds(0.5f);
            yield return Tap(key);
            yield return new WaitForSeconds(2.5f);
            var last = 99f;
            for (var n = 0; n < 30; n++)
            {
                // into the reef, steering sideways onto the branch like a driver would (the keys only drive in 8
                // directions)
                var lat = Vector3.ProjectOnPlane(f.LeftNode.transform.position - _robot.transform.position, toReef);
                lat.y = 0;
                yield return Drive(toReef + lat * 10f, 0.15f);
                var d = Vector3.Dot(f.LeftNode.transform.position - _robot.transform.position, toReef);
                if (Mathf.Abs(d - last) < 0.005f && lat.magnitude < 0.03f) break;
                last = d;
            }
        }
        else if (early)
        {
            // aligned at stow, then the level and place together: the arm is still on its way up
            Hold(Key.Q);
            yield return new WaitForSeconds(3f);
            Hold(key);
            yield return new WaitForSeconds(0.05f);
        }
        else
        {
            yield return Tap(key);
            yield return new WaitForSeconds(2.5f);
            Hold(Key.Q);
            yield return new WaitForSeconds(3f);
        }
        L($"score {level} at node: {Status()} robot->node {Local(f.LeftNode.transform.position)}");
        if (level == "L1")
        {
            // height of the trough's lip under the claw, and how far the coral and claw clear it
            var lip = float.NegativeInfinity;
            var lipZ = 0f;
            for (var z = 0.36f; z < 0.46f; z += 0.005f) // the lip stands in front of the trough's floor
                if (Physics.Raycast(_robot.transform.TransformPoint(0.2f, 1.2f, z), Vector3.down, out var rh, 1.2f, LayerMask.GetMask("Default", "Field"), QueryTriggerInteraction.Ignore) && rh.point.y > lip)
                { lip = rh.point.y; lipZ = z; }
            var profile = "";
            for (var z = 0.32f; z < 0.72f; z += 0.02f)
                if (Physics.Raycast(_robot.transform.TransformPoint(0.2f, 1.2f, z), Vector3.down, out var rp, 1.2f, LayerMask.GetMask("Default", "Field"), QueryTriggerInteraction.Ignore))
                    profile += $" z{z:F2}:{rp.point.y:F3}";
            L($"L1 trough profile:{profile}");
            var heldC = NodeOf("Coral").GetCurrentState() != null && HasCoral ? Find("Coral Claw State").position : Vector3.zero;
            var lowest = "";
            var low = float.PositiveInfinity;
            foreach (var n in new[] { "Claw", "Arm" })
                foreach (var c in Find(n).GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger && c.bounds.max.z > -99f))
                    if (_robot.transform.InverseTransformPoint(c.bounds.center).z > 0.25f && c.bounds.min.y < low && c.transform.GetComponentInParent<PieceCtrl>() == null) { low = c.bounds.min.y; lowest = c.name; }
            L($"L1 clearance: lip y={lip:F3} at z={lipZ:F3} robot->held coral centre {Local(heldC)} coral bottom over lip {heldC.y - 0.057f - lip:F3}m lowest claw part {lowest} over lip {low - lip:F3}m");
        }
        if (level == "L1") Result("L1 pose leaves the robot flat on the carpet", _robot.transform.position.y < 0.01f && Vector3.Angle(Vector3.up, _robot.transform.up) < 1.5f,
            $"robotY={_robot.transform.position.y:F3}m tilt={Vector3.Angle(Vector3.up, _robot.transform.up):F1}deg");
        if (level == "L1") // what the claw and arm touch at L1 (they should be clear of the reef), and the corals in this trough
        {
            foreach (var n in new[] { "Claw", "Arm" })
                if (Find(n)) Object.Destroy(Find(n).gameObject.AddComponent<MissDaisyContactLog>(), 3f);
            // anything of the robot (and its held coral) inside the reef at the L1 pose
            var reefMask = LayerMask.GetMask("Default", "Field");
            var inReef = new List<string>();
            foreach (var rc in _robot.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger))
                foreach (var o in Physics.OverlapBox(rc.bounds.center, rc.bounds.extents, Quaternion.identity, reefMask, QueryTriggerInteraction.Ignore))
                    if (!o.transform.IsChildOf(_robot.transform) && o.name != "CarpetCollider" && Physics.ComputePenetration(rc, rc.transform.position, rc.transform.rotation,
                            o, o.transform.position, o.transform.rotation, out _, out var dep) && dep > 0.005f)
                        inReef.Add($"{rc.name}->{o.name}:{dep * 100:F1}cm");
            var heldCtl = NodeOf("Coral").controller;
            if (heldCtl && Find("Held Coral Body"))
                foreach (var o in Physics.OverlapSphere(heldCtl.transform.position, 0.15f, reefMask, QueryTriggerInteraction.Ignore).Where(o => o.name != "CarpetCollider"))
                    if (Physics.ComputePenetration(Find("Held Coral Body").GetComponent<Collider>(), Find("Held Coral Body").position, Find("Held Coral Body").rotation,
                            o, o.transform.position, o.transform.rotation, out _, out var dep2) && dep2 > 0.005f)
                        inReef.Add($"heldCoral->{o.name}:{dep2 * 100:F1}cm");
            L($"score L1 in reef: [{string.Join(", ", inReef.Distinct())}]");
            var held = NodeOf("Coral").controller;
            if (held)
                foreach (var hc in held.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).Take(3))
                    L($"score L1 held collider {hc.name} parent={hc.transform.parent?.name} layer={LayerMask.LayerToName(hc.gameObject.layer)} prio={hc.layerOverridePriority} " +
                      $"exclude={hc.excludeLayers.value} include={hc.includeLayers.value} body={(hc.attachedRigidbody ? hc.attachedRigidbody.name + " ex=" + hc.attachedRigidbody.excludeLayers.value : "-")} enabled={hc.enabled}");
            foreach (var c in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(c => c.name.StartsWith("Coral")))
            {
                var lc = _robot.transform.InverseTransformPoint(c.transform.position);
                if (Mathf.Abs(lc.x) < 0.5f && lc.z > 0.2f && lc.z < 0.9f && lc.y < 0.8f)
                    L($"score L1 trough coral {c.name} at (robot) {lc:F3}");
            }
        }
        var piece = NodeOf("Coral").controller;
        var br = Object.FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None)
            .SelectMany(p => ((Array)Branches.GetValue(p)).Cast<object>())
            .Where(b => level == "L1" || b.GetType().GetProperty("TargetHeight").GetValue(b).ToString() == level)
            .OrderBy(b => ((Vector3)b.GetType().GetField("center").GetValue(b) - (piece ? piece.transform.position : Vector3.zero)).sqrMagnitude).First();
        var bc = (Vector3)br.GetType().GetField("center").GetValue(br);
        var bq = (Quaternion)br.GetType().GetField("rotation").GetValue(br);
        string Rel() => piece ? (Quaternion.Inverse(bq) * (piece.transform.position - bc)).ToString("F2") : "-";
        L($"score {level} coral(robot)={(piece ? Local(piece.transform.position) : "-")} branch(robot)={Local(bc)} coralInBranch={Rel()} coralAxis(robot)={(piece ? _robot.transform.InverseTransformDirection(piece.transform.rotation * Vector3.forward).ToString("F2") : "-")}");
        var capAt = (bc + piece.transform.position) * 0.5f;
        Capture($"{tag.Replace(' ', '_')}-1-prescore", capAt, toReef);
        if (piece) piece.gameObject.AddComponent<MissDaisyContactLog>();
        var robotLog = _robot.gameObject.AddComponent<MissDaisyContactLog>();
        foreach (var a in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Algae" && piece &&
                     (p.transform.position - piece.transform.position).magnitude < 1.2f))
            L($"score {level} algae {a.name} near coral at (robot) {Local(a.transform.position)}");
        Hold(Key.K);
        if (early) Release(key);
        for (var k = 0; k < (early ? 80 : 30); k++)
        {
            yield return new WaitForSeconds(0.05f);
            if (k == 3 || k == 7 || k == 14 || k == 29) Capture($"{tag.Replace(' ', '_')}-2-place-{k:D2}", capAt, toReef);
            if (k == 7 && overIntake && level == "L3")
                CardShot("both-sides", _robot.transform.position + toReef * 0.25f + Vector3.up * 0.75f, toReef);
            L($"score {level} release +{(k + 1) * 0.05f:F2}s coral(robot)={(piece ? Local(piece.transform.position) : "-")} " +
              $"vel={(piece && piece.GetComponent<Rigidbody>() ? piece.GetComponent<Rigidbody>().velocity.ToString("F2") : "-")} shoulder={(Field("shoulder") as GenericJoint)?.GetSingleAxisAngle(JointAxis.X):F1} elev={(Field("elevator") as GenericElevator)?.GetElevatorHeight():F1}in sp={_robot.CurrentSetpoint}");
        }
        Release(Key.K);
        Object.Destroy(robotLog);
        if (level != "L1")
        {
            // back off the reef like 341's ScoreBack (and a driver): the branch has to keep the coral
            Release(Key.Q);
            yield return Drive(-toReef, 0.4f);
        }
        for (var k = 0; k < 6; k++)
        {
            yield return new WaitForSeconds(0.5f);
            L($"score {level} +{(k + 1) * 0.5f:F1}s coralInBranch={Rel()} coral(robot)={(piece ? Local(piece.transform.position) : "-")} branchesScored={ScoredBranches()}");
        }
        Capture($"{tag.Replace(' ', '_')}-3-settled", capAt, toReef);
        Release(Key.Q);
        var after = Score();
        if (level == "L1")
            Result($"score {tag}", after.CoralPoints >= bTrough + 2, $"trough (coral points) {bTrough}->{after.CoralPoints} | {Pts(after)}");
        else
            Result($"score {tag}", ScoredBranches(level) > bBranch, $"{level} branches {bBranch}->{ScoredBranches(level)} | {Pts(after)}");
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
        Put(algae, t.position + t.forward * 1.2f + Vector3.up * 0.25f, Quaternion.identity);
        yield return new WaitForSeconds(1f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        var end = Time.time + 4f;
        while (Time.time < end && !HasAlgae)
            yield return Drive(t.forward, 0.25f);
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
        // line up on the opening (centred on the processor itself, not on its thin scoring slab)
        PlaceRobot(new Vector3(proc.transform.position.x, 0, target.z) - wall * 1.6f, wall);
        L($"processor transform {proc.transform.position:F2}");
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        yield return Tap(Key.U);
        yield return new WaitForSeconds(2f);
        var heldAlgae = NodeOf("Algae").controller;
        L($"processor start robot={_robot.transform.position:F2} target={target:F2} algae(robot)={(heldAlgae ? Local(heldAlgae.transform.position) : "-")} trigger(robot)={Local(target)} trigRot={(trig ? trig.transform.rotation.eulerAngles : Vector3.zero):F0} trigSize={(trig ? Vector3.Scale(trig.size, trig.transform.lossyScale) : Vector3.zero):F3}");
        var rlog = _robot.gameObject.AddComponent<MissDaisyContactLog>();
        // map the processor opening: depth of the first non-robot / non-piece hit straight into the wall
        var fieldMask = ~LayerMask.GetMask("Robot", "Coral", "Algae");
        foreach (var pr in Object.FindObjectsByType<ProcessorScorer>(FindObjectsSortMode.None))
        {
            var tr = (BoxCollider)typeof(ProcessorScorer).GetField("scoreTrigger", F).GetValue(pr);
            var tc = tr.bounds.center;
            var wz = new Vector3(0, 0, Mathf.Sign(tc.z));
            L($"processor {pr.Alliance} trigger bounds {tr.bounds.min:F3}..{tr.bounds.max:F3}");
            for (var y = 0.1f; y < 0.75f; y += 0.1f)
            {
                var row = "";
                for (var i = -10; i <= 10; i++)
                {
                    var o = new Vector3(tc.x + i * 0.1f, y, tc.z - wz.z * 0.9f);
                    row += Physics.Raycast(o, wz, out var hit, 2f, fieldMask, QueryTriggerInteraction.Ignore) ? $" {Mathf.Abs(hit.point.z):F2}" : "  ---";
                }
                L($"processor {pr.Alliance} wall y={y:F2} x={tc.x - 1:F2}..{tc.x + 1:F2}:{row}");
            }
        }
        var endP = Time.time + 6f;
        var lastZ = -99f;
        while (Time.time < endP && Mathf.Abs(_robot.transform.position.z - lastZ) > 0.01f)
        {
            lastZ = _robot.transform.position.z;
            yield return Drive(wall, 0.5f);
            L($"processor drive robot={_robot.transform.position:F2} yaw={_robot.transform.eulerAngles.y:F0}");
        }
        Object.Destroy(rlog);
        L($"processor after drive robot={_robot.transform.position:F2} yaw={_robot.transform.eulerAngles.y:F0}");
        L($"processor approach: {Status()} trigger(robot)={Local(target)}");
        var dropped = NodeOf("Algae").controller;
        yield return Tap(Key.K);
        for (var k = 0; k < 10 && dropped; k++)
        {
            L($"processor eject +{k * 0.1f:F1}s algae(robot)={Local(dropped.transform.position)} vel={(dropped.GetComponent<Rigidbody>() ? dropped.GetComponent<Rigidbody>().velocity : Vector3.zero):F2}");
            yield return new WaitForSeconds(0.1f);
        }
        var after = Score();
        for (var w = 0f; w < 9f && after.TotalPoints <= before.TotalPoints; w += 0.5f)
        {
            yield return new WaitForSeconds(0.5f);
            after = Score();
        }
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
        PlaceRobot(new Vector3(ourSide * 1.6f, 0, target.z), -toNet);
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        if (box)
            foreach (var c in Physics.OverlapBox(box.bounds.center, Vector3.Scale(box.size, box.transform.lossyScale) / 2, box.transform.rotation).Where(c => c.CompareTag("Algae")))
                L($"barge already in net: {c.name} at {c.transform.position:F2} parent {(c.transform.parent ? c.transform.parent.name : "-")}");
        yield return Tap(Key.P);
        yield return new WaitForSeconds(2.5f);
        var end = Time.time + 3f;
        while (Time.time < end && Mathf.Abs(_robot.transform.position.x) > BargeStandOff)
            yield return Drive(toNet, 0.15f);
        yield return new WaitForSeconds(1f);
        L($"barge: {Status()} net(robot)={Local(target)} netSize={(box ? box.bounds.size : Vector3.zero):F2}");
        Capture("barge-lineup", _robot.transform.position + Vector3.up * 0.9f, toNet); // lined up to throw (a tutorial card shot)
        CardShot("barge", _robot.transform.position + toNet * 0.15f + Vector3.up * 0.8f, toNet);
        var thrown = NodeOf("Algae").controller;
        L($"barge algae before throw (robot) {(thrown ? Local(thrown.transform.position) : "-")}");
        yield return Tap(Key.K);
        for (var k = 0; k < 25 && thrown; k++)
        {
            L($"barge throw +{k * 0.1f:F1}s algae(robot)={Local(thrown.transform.position)} vel={(thrown.GetComponent<Rigidbody>() ? thrown.GetComponent<Rigidbody>().velocity : Vector3.zero):F2}");
            yield return new WaitForSeconds(0.1f);
        }
        // it has to stay in the net once the robot stows and backs away; on the way down the elevator must not drop
        // below its base (it was seen flying under the robot and back up)
        yield return Tap(Key.Z);
        var el = Field("elevator") as GenericElevator;
        var stage = Find("Stage1");
        var carriage = Find("Carriage");
        float minEl = 99f, minStage = 99f, minCar = 99f, stage0 = _robot.transform.InverseTransformPoint(stage.position).y;
        var elTrace = new List<string>();
        for (var k = 0; k < 100; k++)
        {
            yield return new WaitForFixedUpdate();
            minEl = Mathf.Min(minEl, el.GetElevatorHeight());
            minStage = Mathf.Min(minStage, _robot.transform.InverseTransformPoint(stage.position).y);
            minCar = Mathf.Min(minCar, _robot.transform.InverseTransformPoint(carriage.position).y);
            if (k % 5 == 0) elTrace.Add(el.GetElevatorHeight().ToString("F1"));
        }
        L($"barge stow elevator: min height {minEl:F1}in stage1 min y {minStage:F3} (start {stage0:F3}) carriage min y {minCar:F3} trace {string.Join(",", elTrace)}");
        var stageRest = _robot.transform.InverseTransformPoint(stage.position).y;
        Result("barge stow: elevator stays above its base", minEl > -1f && minStage > stageRest - 0.01f,
            $"minHeight={minEl:F1}in stage1 dipped {stageRest - minStage:F3}m below where it settled");
        yield return Drive(-toNet, 0.8f);
        yield return new WaitForSeconds(2f);
        var after = Score();
        var inNet = thrown && box && Physics.OverlapBox(box.bounds.center, Vector3.Scale(box.size, box.transform.lossyScale) / 2, box.transform.rotation)
            .Any(c => c.transform.IsChildOf(thrown.transform) || c.transform == thrown.transform);
        Result("score barge", inNet && after.NetPoints >= 4, $"thrown algae in net={inNet} at {(thrown ? thrown.transform.position.ToString("F2") : "-")} | {Pts(before)} -> {Pts(after)}");
    }

    // Deep cage: back the deployed climber into an alliance cage, climb, check off the ground with the cage caught.
    IEnumerator Climb(float lateral = 0f)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Station);
        // our side of the barge is the side our reef is on; back into the cage nearest the robot from there
        var ourSide = Mathf.Sign(Object.FindObjectsByType<Reef>(FindObjectsSortMode.None).First(r => r.Alliance == _robot.Alliance).transform.position.x);
        var cage = GameObject.FindGameObjectsWithTag("Cage").OrderBy(c => Mathf.Abs(c.transform.position.x)).ThenBy(c => Mathf.Abs(c.transform.position.z)).First();
        var cs = _robot.GetComponent<ClimbScorer>();
        // the harpoon goes in between the two posts on our side of the cage
        var posts = cage.GetComponentsInParent<Rigidbody>().First().GetComponentsInChildren<Collider>().Where(c => c.CompareTag("Cage")).ToList();
        var away = new Vector3(ourSide, 0, 0);
        var near = posts.OrderByDescending(c => c.bounds.center.x * ourSide).Take(2).ToList();
        var target = near[0];
        var cp = (near[0].bounds.center + near[1].bounds.center) * 0.5f;
        PlaceRobot(new Vector3(cp.x + away.x * 1.1f, 0, cp.z + lateral), -away); // the climber is on the front
        L($"climb lateral start {lateral:F3}");
        yield return new WaitForSeconds(0.5f);
        var climberT = Find("Climber");
        Capture("climb-1-stowed", climberT.position, -away);
        for (var k = 0; k < 3 && _robot.CurrentSetpoint != Games.Reefscape.Enums.ReefscapeSetpoints.Climb; k++) yield return Tap(Key.C);
        yield return new WaitForSeconds(3f);
        Capture("climb-2-deployed", climberT.position, -away);
        // drive in until both hooks have folded in and sprung back out behind the posts
        var hooks = _robot.GetComponentsInChildren<RobotFramework.Components.GenericAnimationJoint>().Where(j => j.name.Contains("Clicker")).ToList();
        var flexed = new bool[hooks.Count];
        var held = 0;
        var end = Time.time + 16f;
        while (Time.time < end)
        {
            // ease in like a driver lining up on a cage: short pushes, steering onto the middle of the two near posts
            // (the harpoon's pocket onto the posts where they pass its height, not the robot's centre onto them)
            var pk0 = Find("Cage Scoring Trigger");
            var aim = pk0 ? pk0.position : _robot.transform.position;
            var mid = (near[0].ClosestPoint(aim) + near[1].ClosestPoint(aim)) * 0.5f;
            var lat = Vector3.ProjectOnPlane(mid - aim, away);
            lat.y = 0;
            yield return Drive(-away + lat * 10f, 0.07f);
            var cageNow = posts.Aggregate(Vector3.zero, (acc, c) => acc + c.bounds.center) / Mathf.Max(1, posts.Count);
            var pocket = Find("Cage Scoring Trigger");
            if (pocket)
            {
                // the target post's axis point at the pocket's height, in the pocket frame (z out of the mouth)
                var cap = (CapsuleCollider)target;
                var ax = cap.transform.TransformDirection(cap.direction == 0 ? Vector3.right : cap.direction == 1 ? Vector3.up : Vector3.forward);
                var cc = cap.bounds.center;
                var onAxis = cc + ax * Vector3.Dot(pocket.position - cc, ax);
                L($"climb post in pocket frame {pocket.InverseTransformPoint(onAxis):F3} postTilt={Vector3.Angle(ax, Vector3.up):F0}");
            }
            L($"climb approach cage(robot)={Local(cageNow)} hooks={string.Join(",", hooks.Select(h => Quaternion.Angle(Quaternion.identity, h.transform.localRotation).ToString("F0")))} " +
              string.Join(" ", hooks.Select(h =>
              {
                  var m = h.transform.Find("Move Away").GetComponent<BoxCollider>();
                  var hits = Physics.OverlapBox(m.transform.position, Vector3.Scale(m.size, m.transform.lossyScale) / 2, m.transform.rotation)
                      .Where(c => c.gameObject.layer != h.gameObject.layer).Select(c => c.name);
                  return $"{h.name}:[{string.Join("/", hits)}]";
              })));
            for (var i = 0; i < hooks.Count; i++)
                if (Quaternion.Angle(Quaternion.identity, hooks[i].transform.localRotation) > 10f) flexed[i] = true;
            // a driver stops pushing once both hooks have snapped shut behind the posts (pushing on swings the cage away)
            held = flexed.All(f => f) && hooks.All(h => Quaternion.Angle(Quaternion.identity, h.transform.localRotation) < 4f)
                ? held + 1 : 0;
            if (held >= 2) break;
        }
        L($"climb hooks flexed={string.Join(",", flexed)}");
        Capture("climb-3-at-cage", climberT.position, -away);
        L($"climb at cage: {Status()} triggered={cs.ScoringTriggered} cage(robot)={Local(cp)} hooks={string.Join(",", _robot.GetComponentsInChildren<RobotFramework.Components.GenericAnimationJoint>().Where(j => j.name.Contains("Clicker")).Select(j => Quaternion.Angle(Quaternion.identity, j.transform.localRotation).ToString("F0")))}");
        foreach (var h in _robot.GetComponentsInChildren<Transform>().Where(t => t.name.EndsWith("Clicker") || t.name == "Move Away" || t.name == "Cage Scoring Trigger"))
            L($"climb geometry {h.parent.name}/{h.name} at (robot) {Local(h.position)}");
        foreach (var c in cage.transform.root.GetComponentsInChildren<CapsuleCollider>().Where(c => c.CompareTag("Cage")))
            L($"climb geometry post {c.name} centre (robot) {Local(c.bounds.center)} y {c.bounds.min.y - _robot.transform.position.y:F2}..{c.bounds.max.y - _robot.transform.position.y:F2}");
        var climbLog = _robot.gameObject.AddComponent<MissDaisyContactLog>();
        var climberLog = Find("Climber").gameObject.AddComponent<MissDaisyContactLog>();
        var armLog = Find("Arm").gameObject.AddComponent<MissDaisyContactLog>();
        var clawLog = Find("Claw").gameObject.AddComponent<MissDaisyContactLog>();
        yield return Tap(Key.C);
        // a hook still open: the climber waits at the slide-up angle, and the driver keeps pushing in until it clicks
        for (var k = 0; k < 10 && hooks.Any(h => Quaternion.Angle(Quaternion.identity, h.transform.localRotation) > 2f); k++)
            yield return Drive(-away, 0.07f);
        L($"climb latched after pushing: hooks={string.Join(",", hooks.Select(h => Quaternion.Angle(Quaternion.identity, h.transform.localRotation).ToString("F0")))}");
        for (var k = 0; k < 40; k++)
        {
            yield return new WaitForSeconds(0.1f);
            var pk = Find("Cage Scoring Trigger");
            var hookAngles = string.Join(",", _robot.GetComponentsInChildren<Transform>().Where(t => t.name.EndsWith("Hook Clicker"))
                .Select(t => Quaternion.Angle(Quaternion.identity, t.localRotation).ToString("F0")));
            var nearPosts = posts.OrderBy(c => (c.bounds.center - pk.position).sqrMagnitude).Take(2)
                .Select(c => pk.InverseTransformPoint(c.ClosestPoint(pk.position)).ToString("F3"));
            if (k < 30) L($"climb pull trace +{(k + 1) * 0.1f:F1}s climber={(Field("climber") as GenericJoint)?.GetSingleAxisAngle(JointAxis.X):F0} shoulder={Mathf.DeltaAngle(0, (Field("shoulder") as GenericJoint).GetSingleAxisAngle(JointAxis.X)):F1} wrist={Mathf.DeltaAngle(0, (Field("wrist") as GenericJoint).GetSingleAxisAngle(JointAxis.Y)):F1} elev={(Field("elevator") as GenericElevator).GetElevatorHeight():F1}in hooks={hookAngles} posts(pocket)={string.Join(" ", nearPosts)}");
            if (k % 5 != 4) continue;
            if (pk) L($"climb pulling post(pocket)={pk.InverseTransformPoint(target.bounds.center):F3}");
            L($"climb pulling +{(k + 1) * 0.1f:F1}s robotY={_robot.transform.position.y:F2} climber={(Field("climber") as GenericJoint)?.GetSingleAxisAngle(JointAxis.X):F0} cage(robot)={Local(cage.transform.position)} triggered={cs.ScoringTriggered}");
        }
        Object.Destroy(climbLog);
        Object.Destroy(climberLog);
        Object.Destroy(armLog);
        Object.Destroy(clawLog);
        Capture("climb-4-pulled", climberT.position, -away);
        var trig = (BoxCollider)typeof(ClimbScorer).GetField("cageScoringTrigger", F).GetValue(cs);
        foreach (var cc in cage.GetComponentsInChildren<Collider>().Concat(cage.GetComponents<Collider>()).Distinct().Where(c => c.CompareTag("Cage")).Take(4))
        {
            var cl = cc.ClosestPoint(trig.bounds.center);
            L($"climb cage collider {cc.name}: closest to trigger centre {trig.transform.InverseTransformPoint(cl):F3} trigger half {(trig.size / 2):F3} latch={(Field("climber") as Component)?.GetComponent<FixedJoint>() != null}");
        }
        var dc = _robot.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
        // the whole robot off the carpet, the bumpers too (not just the wheels): its lowest collider point
        var lowest = _robot.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger && c.gameObject.layer == LayerMask.NameToLayer("Robot"))
            .Min(c => c.bounds.min.y);
        var pitch = Vector3.SignedAngle(Vector3.up, _robot.transform.up, _robot.transform.right);
        var body = _robot.GetComponent<Rigidbody>();
        L($"climb body centre of mass (robot) {_robot.transform.InverseTransformPoint(body.worldCenterOfMass):F3} mass {body.mass:F0}kg");
        Result("climb", cs.ScoringTriggered && !dc.IsTouchingGround && lowest > 0.03f,
            $"{Status()} triggered={cs.ScoringTriggered} touchingGround={dc.IsTouchingGround} robotY={_robot.transform.position.y:F2} lowestPoint={lowest:F3}m pitch={pitch:F0}deg");
    }

    [UnityTest]
    [Timeout(1500000)]
    public IEnumerator MissDaisyMatch()
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
        var pieceLayers = new[] { LayerMask.NameToLayer("Coral"), LayerMask.NameToLayer("Algae") };
        var off = _robot.GetComponentsInChildren<Transform>(true)
            .Where(x => x.gameObject.layer != robotLayer && !pieceLayers.Contains(x.gameObject.layer) && !x.GetComponentInParent<PieceCtrl>() &&
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

        var only = (Environment.GetEnvironmentVariable("MD_ONLY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool Run(string section) => only.Length == 0 || only.Contains(section);

        if (Run("arm")) yield return ArmSettle();
        if (Run("drive")) yield return DriveCheck();
        if (Run("pickup")) { yield return GetFloorCoral("pickup test"); yield return GetFloorCoral("pickup test off-centre", 0.2f); yield return GetFloorCoral("pickup test 90", 0f, 90f); }

        // coral: floor pickup + L4, L3, L2, L1; HP station from the intake side
        foreach (var (lvl, key, face) in new[] { ("L4", Key.P, 0), ("L3", Key.O, 1), ("L2", Key.I, 2), ("L1", Key.U, 3) })
        {
            if (!Run("coral")) break;
            yield return GetFloorCoral($"floor coral for {lvl}");
            if (HasCoral) yield return ScoreCoral(lvl, key, face);
        }
        // the same levels scored over the intake side (robot facing away, e.g. with the camera flipped)
        foreach (var (lvl, key, face) in new[] { ("L4", Key.P, 4), ("L3", Key.O, 5), ("L2", Key.I, 0) })
        {
            if (!Run("coral")) break;
            yield return GetFloorCoral($"floor coral for {lvl} over intake");
            if (HasCoral) yield return ScoreCoral(lvl, key, face, overIntake: true);
        }
        if (Run("coral"))
        {
            yield return GetFloorCoral("floor coral off-centre (0.2 m)", 0.2f);
            if (HasCoral) yield return ScoreCoral("L3", Key.O, 2, manual: true);
            yield return GetFloorCoral("floor coral off-centre (-0.2 m)", -0.2f);
            if (HasCoral) yield return ScoreCoral("L2", Key.I, 3, manual: true);
            yield return GetFloorCoral("floor coral turned 45 deg", 0f, 45f);
            yield return GetFloorCoral("floor coral turned 90 deg (pointing at the intake)", 0f, 90f);
            yield return GetFloorCoral("floor coral, robot stopped at the intake", 0f, 0f, still: true);
            yield return FloorCoralKnockedAway("floor coral knocked out of line mid-pickup");
            yield return ReleaseBeforeRising("coral out of the rollers' reach is left alone");
            yield return TwoCoral("two coral in the intake box both pulled");
            yield return GetFloorCoral("floor coral turned 45 deg, robot stopped at the intake", 0f, 45f, still: true);
            yield return GetFloorCoral("floor coral for early place L3");
            if (HasCoral) yield return ScoreCoral("L3", Key.O, 4, early: true);
            // the trough under a face whose L2 is already scored (face 2, from the first round), twice (a stack)
            for (var n = 1; n <= 3; n++)
            {
                yield return GetFloorCoral($"floor coral for L1 under a scored L2 ({n})");
                if (HasCoral) yield return ScoreCoral("L1", Key.U, 2);
            }
        }
        if (only.Contains("grip"))
        {
            yield return RollerTakesIt("roller takes it, across", 0f, 0f);
            yield return RollerTakesIt("roller takes it, off-centre 0.2", 0.2f, 0f);
            yield return RollerTakesIt("roller takes it, off-centre -0.25", -0.25f, 0f);
            yield return RollerTakesIt("roller takes it, half past the roller end 0.3", 0.3f, 0f);
            yield return RollerTakesIt("roller takes it, half past the roller end 0.35", 0.35f, 0f);
            yield return RollerTakesIt("roller takes it, half past the roller end -0.35", -0.35f, 0f);
            yield return RollerTakesIt("roller takes it, half past the roller end 0.4", 0.4f, 0f);
            yield return GetFloorCoral("driven onto a coral half past the roller end 0.35", 0.35f);
            yield return GetFloorCoral("driven onto a coral half past the roller end -0.4", -0.4f);
            yield return RollerTakesIt("roller takes it, turned 30", 0f, 30f);
            yield return RollerTakesIt("roller takes it, turned 60", 0f, 60f);
            yield return RollerTakesIt("roller takes it, end-on", 0f, 90f);
            yield return RollerTakesIt("roller takes it, end-on off-centre", 0.12f, 90f);
        }
        if (only.Contains("flap"))
        {
            yield return UnderFlap("under the first flap", new Vector3(0f, 0.13f, -0.50f), 0f);
            yield return UnderFlap("under the second flap", new Vector3(0f, 0.16f, -0.44f), 0f);
            yield return UnderFlap("under the second flap, off-centre", new Vector3(0.10f, 0.14f, -0.46f), 0f);
            yield return UnderFlap("under the second flap, crooked", new Vector3(0f, 0.15f, -0.46f), 25f);
            yield return UnderFlap("high under the second flap", new Vector3(0f, 0.20f, -0.46f), 0f);
        }
        if (only.Contains("wobble")) yield return L4Wobble();
        if (only.Contains("l1start")) yield return L1FromStow();
        if (only.Contains("knock")) yield return FloorCoralKnockedAway("floor coral knocked out of line mid-pickup");
        if (only.Contains("two")) yield return TwoCoral("two coral in the intake box both pulled");
        if (only.Contains("l1")) // on its own: on top of the "coral" section's stack it would be more than a trough holds
            for (var n = 1; n <= 3; n++)
            {
                yield return GetFloorCoral($"floor coral for L1 stack ({n})");
                if (HasCoral) yield return ScoreCoral("L1", Key.U, 2);
            }
        if (Run("hp"))
        {
            yield return GetStationCoral("HP station (intake side)", -1f);
            if (HasCoral) yield return OtherPieceIntake("holding coral, algae intake pressed");
            if (HasCoral) yield return OuttakeInStow("coral outtake at stow");
        }

        // algae: floor -> processor, floor -> barge, reef low -> processor, reef high -> barge
        if (Run("algae"))
        {
            yield return GetFloorAlgae("floor algae for processor");
            if (HasAlgae) yield return ScoreProcessor();
            yield return GetFloorAlgae("floor algae for barge");
            if (HasAlgae) yield return OtherPieceIntake("holding algae, coral intake pressed");
            if (HasAlgae) yield return ScoreBarge();
            yield return GetReefAlgae("reef algae low", false);
            if (HasAlgae) yield return ScoreProcessor();
            yield return GetReefAlgae("reef algae high", true);
            if (HasAlgae) yield return ScoreBarge();
        }

        if (Run("climb")) yield return Climb();
        if (Run("climb") && _robot.CurrentSetpoint == Games.Reefscape.Enums.ReefscapeSetpoints.Climbed && _robot.transform.position.y > 0.05f)
        {
            // hanging: nothing moves, whatever is pressed
            float El() => (Field("elevator") as GenericElevator).GetElevatorHeight();
            float An(string f) => (Field(f) as GenericJoint).GetSingleAxisAngle(JointAxis.X);
            var e0 = El(); var s0 = An("shoulder"); var c0 = An("climber"); var y0 = _robot.transform.position.y;
            var worst = 0f;
            foreach (var key in new[] { Key.LeftShift, Key.U, Key.I, Key.O, Key.P, Key.C, Key.K })
            {
                yield return Tap(key);
                for (var k = 0; k < 40; k++)
                {
                    yield return new WaitForFixedUpdate();
                    worst = Mathf.Max(worst, Mathf.Abs(El() - e0), Mathf.Abs(Mathf.DeltaAngle(An("shoulder"), s0)) / 5f, Mathf.Abs(Mathf.DeltaAngle(An("climber"), c0)) / 5f);
                }
            }
            Result("once climbed no button moves a mechanism", worst < 1.5f && _robot.transform.position.y > y0 - 0.03f, $"worst {worst:F2} (in, deg/5) {Status()} robotY={_robot.transform.position.y:F2}");
        }
        foreach (var lateral in (System.Environment.GetEnvironmentVariable("MD_CLIMB_LAT") ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Station);
            PlaceRobot(Open, Vector3.forward);
            yield return new WaitForSeconds(8f); // the cage stops swinging
            yield return Climb(float.Parse(lateral, System.Globalization.CultureInfo.InvariantCulture));
        }

        L("SUMMARY\n" + string.Join("\n", _results));
        L($"DONE pass={_results.Count(r => r.Contains(" PASS "))} fail={_results.Count(r => r.Contains(" FAIL "))}");
    }
}
#endif
