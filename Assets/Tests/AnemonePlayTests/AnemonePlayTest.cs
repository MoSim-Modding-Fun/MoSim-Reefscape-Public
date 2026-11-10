// Plays 180 Anemone like a driver in the Reefscape scene and checks real points, not just piece movement: the
// four-bar intake out and back, floor coral backed into, HP station, coral on L1-L4 via auto-align, reef algae (low
// and high), floor algae, processor and net, deep cage climb with the side climber. Logs "[AN]" lines; every check
// ends in a "RESULT <name> PASS|FAIL ..." line. ANEMONE_ONLY=floor,l4,... runs a subset.
//   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter AnemonePlayTest -testResults r.xml -logFile l.log
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

// Each rendered frame: how deep any coral near the tube sits in the tube's walls (a capsule the coral's size at its pose
// against the wall boxes), per phase of the test.
[DefaultExecutionOrder(10000)]
public class AnemoneTubeWatch : MonoBehaviour
{
    public Transform bore;
    public Collider[] walls;
    public string phase = "start";
    public Func<string> status;
    public readonly Dictionary<string, (float max, int deep, int frames, string worst)> phases = new();
    CapsuleCollider _probe;

    void Start()
    {
        var go = new GameObject("Tube Watch Probe");
        go.layer = 2;
        _probe = go.AddComponent<CapsuleCollider>();
        _probe.radius = 0.0572f;
        _probe.height = 0.30f;
        _probe.direction = 2;
        _probe.isTrigger = true;
    }

    void LateUpdate()
    {
        if (!bore) return;
        foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None))
        {
            if (p.PieceName != "Coral") continue;
            var l = bore.InverseTransformPoint(p.transform.position);
            if (l.z < -0.45f || l.z > 0.7f || new Vector2(l.x, l.y).magnitude > 0.25f) continue;
            // the coral's own ring of boxes: it is hollow, so a lip in its open end is not phasing
            var depth = 0f;
            var ring = (Collider[])typeof(PieceCtrl).GetField("_colliders", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(p);
            foreach (var c in ring)
            {
                if (!c || c is not BoxCollider) continue;
                foreach (var w in walls)
                    if (Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, w, w.transform.position, w.transform.rotation, out _, out var d))
                        depth = Mathf.Max(depth, d);
            }
            phases.TryGetValue(phase, out var e);
            e.frames++;
            if (depth > 0.01f) e.deep++;
            if (depth > e.max)
            {
                e.max = depth;
                var a = bore.InverseTransformDirection(p.transform.forward);
                e.worst = $"at bore(x,y,z)=({l.x:F3},{l.y:F3},{l.z:F3}) axis off {Vector3.Angle(Vector3.forward, new Vector3(a.x, a.y, Mathf.Abs(a.z))):F0} deg {status?.Invoke()}";
            }
            phases[phase] = e;
        }
    }
}

// A floor coral each physics step: when it first sinks below the carpet, the steps and contacts leading up to it.
public class AnemoneCoralTrack : MonoBehaviour
{
    public Transform robot;
    public string label;
    readonly Queue<string> _recent = new();
    float _minY = 9f;
    bool _sank;

    void OnCollisionStay(Collision c) => OnCollisionEnter(c);
    void OnCollisionEnter(Collision c)
    {
        var n = c.collider.name;
        if (n.Contains("Carpet") || _sank) return;
        _recent.Enqueue($"{Time.fixedTime:F3} touch {n} on {c.collider.transform.parent?.name} impulse {c.impulse.magnitude:F2} n={c.GetContact(0).normal:F2}");
        while (_recent.Count > 40) _recent.Dequeue();
    }

    void FixedUpdate()
    {
        var rb = GetComponent<Rigidbody>();
        var y = rb.position.y;
        _minY = Mathf.Min(_minY, y);
        if (_sank) return;
        _recent.Enqueue($"{Time.fixedTime:F3} pos(robot) {robot.InverseTransformPoint(rb.position):F3} y {y:F3} v {rb.velocity:F2} long(robot) {robot.InverseTransformDirection(transform.forward):F2}");
        while (_recent.Count > 40) _recent.Dequeue();
        if (y < 0.02f)
        {
            _sank = true;
            Debug.Log($"[AN] {label} SANK:\n" + string.Join("\n", _recent));
        }
    }

    void OnDestroy() => Debug.Log($"[AN] {label} lowest y {_minY:F3} sank={_sank}");
}

// Keeps the match enabled and the clock stopped while the test drives.
public class AnemoneKeepEnabled : MonoBehaviour
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

public class AnemonePlayTest
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string Meta = "Assets/Prefabs/Reefscape/Robots/Mods/SPAM/180/Anemone.asset";
    ReefscapeRobotBase _robot;
    Keyboard _kb;
    Key[] _held = Array.Empty<Key>();
    Vector3 _wDir, _dDir;
    readonly List<string> _results = new();
    HashSet<string> _only;

    static void L(string s) => Debug.Log("[AN] " + s);

    // ANEMONE_SHOTS=dir: side-on frames of each reef score (needs graphics)
    Camera _cam;
    RenderTexture _rt;

    // a frame from the robot's side (or another robot-local direction) looking at a robot-local point
    void ShotAt(string name, Vector3 localTarget, Vector3 localFrom, float dist = 1.5f)
    {
        var shots = Environment.GetEnvironmentVariable("ANEMONE_SHOTS");
        if (string.IsNullOrEmpty(shots)) return;
        if (!_cam)
        {
            _cam = new GameObject("ShotCam").AddComponent<Camera>();
            _cam.enabled = false;
            _cam.fieldOfView = 35;
            _rt = new RenderTexture(960, 720, 24);
            _cam.targetTexture = _rt;
        }
        var t = _robot.transform;
        var target = t.TransformPoint(localTarget);
        _cam.transform.position = target + t.TransformDirection(localFrom.normalized) * dist;
        _cam.transform.LookAt(target);
        var dir = System.IO.Path.Combine(shots, name);
        System.IO.Directory.CreateDirectory(dir);
        Shot(dir, System.IO.Directory.GetFiles(dir, "*.png").Length);
    }

    void Shot(string dir, int n)
    {
        if (!_cam) return;
        _cam.Render();
        RenderTexture.active = _rt;
        var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"{n:000}.png"), tex.EncodeToPNG());
        Object.Destroy(tex);
    }

    // ANEMONE_VIDEO=dir: whole clips, one PNG per frame at a fixed 30 fps of game time (encode with ffmpeg)
    readonly List<AnemoneRecorder> _rec = new();

    void StartVideo(string name, Vector3 localTarget, Vector3 localFrom, float dist)
    {
        var dir = Environment.GetEnvironmentVariable("ANEMONE_VIDEO");
        if (string.IsNullOrEmpty(dir)) return;
        Time.captureFramerate = 30;
        var r = new GameObject("Rec " + name).AddComponent<AnemoneRecorder>();
        r.Init(System.IO.Path.Combine(dir, name), _robot.transform, localTarget, localFrom, dist);
        _rec.Add(r);
    }

    void StopVideos()
    {
        foreach (var r in _rec) if (r) Object.Destroy(r.gameObject);
        _rec.Clear();
        Time.captureFramerate = 0;
    }

    void Result(string name, bool pass, string detail)
    {
        var line = $"RESULT {name} {(pass ? "PASS" : "FAIL")} {detail}";
        _results.Add(line);
        L(line);
    }

    bool Run(string key) => _only == null || _only.Contains(key);

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
    float FieldF(string name) => (float)Field(name);

    string Local(Vector3 w)
    {
        var l = _robot.transform.InverseTransformPoint(w);
        return $"({l.x:F2},{l.y:F2},{l.z:F2})";
    }

    float Arm() => FieldF("armCadAngle") + FieldF("armSign") * Mathf.DeltaAngle(0, ((GenericJoint)Field("arm")).GetSingleAxisAngle(JointAxis.X));
    float IntakeAngle() => FieldF("intakeRestAngle") + FieldF("intakeSign") * Mathf.DeltaAngle(0, ((GenericJoint)Field("intakeBar")).GetSingleAxisAngle(JointAxis.X));
    float ClimberAngle() => FieldF("climberSign") * Mathf.DeltaAngle(0, ((GenericJoint)Field("climber")).GetSingleAxisAngle(JointAxis.Z));
    float Elev() => ((GenericElevator)Field("elevator")).GetElevatorHeight() / 39.37f;

    string Status() =>
        $"sp={_robot.CurrentSetpoint} mode={_robot.CurrentRobotMode} drop={_robot.CurrentCoralStationMode.DropType} " +
        $"elev={Elev():F3}m arm={Arm():F1} intake={IntakeAngle():F1} climber={ClimberAngle():F1} coral={HasCoral} algae={HasAlgae} " +
        $"robotY={_robot.transform.position.y:F3} tilt={Vector3.Angle(_robot.transform.up, Vector3.up):F0}";

    // ---------------------------------------------------------------- score data

    static readonly FieldInfo BlueData = typeof(GameSystems.Scoring.ScoreHandler<ReefscapeScoreData, ReefscapeScoreUI>).GetField("BlueScoreData", F);
    static readonly FieldInfo RedData = typeof(GameSystems.Scoring.ScoreHandler<ReefscapeScoreData, ReefscapeScoreUI>).GetField("RedScoreData", F);

    // the score object is refilled every frame: copy it
    ReefscapeScoreData Score()
    {
        var h = Object.FindFirstObjectByType<ReefscapeScoreHandler>();
        if (!h) return new ReefscapeScoreData();
        var d = (ReefscapeScoreData)(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? BlueData : RedData).GetValue(h);
        return (ReefscapeScoreData)typeof(object).GetMethod("MemberwiseClone", F).Invoke(d, null);
    }

    static string Pts(ReefscapeScoreData d) =>
        $"coral={d.CoralPoints} trough={d.TroughPoints} net={d.NetPoints} proc={d.ProcessorPoints} climb={d.ClimbPoints} park={d.ParkPoints} total={d.TotalPoints}";

    static readonly FieldInfo Branches = typeof(ReefPoleScorer).GetField("_branches", F);

    int ScoredBranches(string height = null) => Object.FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None)
        .SelectMany(p => ((Array)Branches.GetValue(p) ?? Array.Empty<object>()).Cast<object>())
        .Count(b => (bool)b.GetType().GetProperty("Scored").GetValue(b) &&
                    (height == null || b.GetType().GetProperty("TargetHeight").GetValue(b).ToString() == height));

    // ---------------------------------------------------------------- robot handling

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

    IEnumerator Drive(Vector3 worldDir, float seconds)
    {
        var keys = DriveKeys(worldDir);
        Hold(keys);
        yield return new WaitForSeconds(seconds);
        Release(keys);
        yield return new WaitForSeconds(0.4f);
    }

    IEnumerator Reset(Games.Reefscape.Enums.ReefscapeRobotMode mode, DropType drop)
    {
        Keys();
        if (_robot.CurrentSetpoint is Games.Reefscape.Enums.ReefscapeSetpoints.Climb or Games.Reefscape.Enums.ReefscapeSetpoints.Climbed)
        {
            yield return Tap(Key.C);
            yield return new WaitForSeconds(1f);
        }
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

    // ---------------------------------------------------------------- mechanisms

    // Stow pose, then the intake out and back in (the four-bar closes its loop on hinges).
    // logs the four-bar every 0.2 s (finds when the stowed linkage collapses)
    void WatchIntake()
    {
        var crank = (GenericJoint)Field("intakeBar");
        var headT = Find("IntakeHead");
        _robot.StartCoroutine(Watch());
        IEnumerator Watch()
        {
            for (var i = 0; i < 60; i++)
            {
                L($"watch t={Time.time:F1}: crank={Mathf.DeltaAngle(0, crank.GetSingleAxisAngle(JointAxis.X)):F1} " +
                  $"head={Local(headT.position)} sp={Field("_target")} robot={_robot.transform.position:F2}");
                yield return new WaitForSeconds(0.2f);
            }
        }
    }

    IEnumerator Mechanisms()
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(2f);
        var head = Find("IntakeHead");
        var top = Find("IntakeTopBar");
        string Loop()
        {
            var hinges = _robot.GetComponentsInChildren<Joint>().Where(h => h.transform == head || h.connectedBody && h.connectedBody.transform == head);
            var gap = hinges.Select(h => (h.transform.TransformPoint(h.anchor) - h.connectedBody.transform.TransformPoint(h.connectedAnchor)).magnitude);
            return $"head={Local(head.position)} topBar={Local(top.position)} pinGaps={string.Join("/", gap.Select(g => g.ToString("F3")))}";
        }
        L($"stow: {Status()} {Loop()}");
        {
            // the four-bar as Unity has it: pivots, pins and link lengths (CAD: bar 0.288, top 0.330, coupler 0.076, ground 0.111)
            var barT = Find("IntakeBottomBar");
            var hs = _robot.GetComponentsInChildren<Joint>().Where(h => h.transform == head || h.connectedBody && h.connectedBody.transform == head).ToList();
            Vector3 W(Joint h) => h.transform.TransformPoint(h.anchor);
            var pinB = W(hs.First(h => h.transform == barT || h.connectedBody && h.connectedBody.transform == barT));
            var pinT = W(hs.First(h => h.transform == top || h.connectedBody && h.connectedBody.transform == top));
            L($"four-bar: bar {(pinB - barT.position).magnitude:F3} top {(pinT - top.position).magnitude:F3} coupler {(pinT - pinB).magnitude:F3} " +
              $"ground {(top.position - barT.position).magnitude:F3} axes {string.Join(" ", hs.Select(h => h.transform.TransformDirection(h.axis).ToString("F2")))} " +
              $"S={Local(barT.position)} O={Local(top.position)} B={Local(pinB)} T={Local(pinT)}");
        }
        var kick = Find("Kicker");
        string Kick()
        {
            // the kicker rides the four-bar: its pin on the top bar, its slot on the bottom bar's pin
            var roller = kick.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name.StartsWith("Kicker Shaft"));
            var slot = kick.GetComponents<ConfigurableJoint>().FirstOrDefault();
            var gap = slot ? Vector3.ProjectOnPlane(kick.TransformPoint(slot.anchor) - slot.connectedBody.transform.TransformPoint(slot.connectedAnchor),
                kick.TransformDirection(slot.axis)).magnitude : -1f;
            return $"kicker roller={(roller ? Local(roller.position) : "-")} slot off pin {gap * 1000:F1} mm";
        }
        L($"stow kicker: {Kick()}");
        var contacts = new List<string>();
        foreach (var n in new[] { "IntakeBottomBar", "IntakeTopBar", "IntakeHead" })
            Find(n).gameObject.AddComponent<AnemoneContactLog>().sink = contacts;
        Result("stow pose", Mathf.Abs(Arm() - 46.4f) < 4f && Mathf.Abs(Elev()) < 0.03f && IntakeAngle() > 70f, Status());
        Hold(Key.LeftShift);
        {
            var barT = Find("IntakeBottomBar");
            var r0 = new[] { barT.localRotation, top.localRotation, head.localRotation };
            var p0 = head.localPosition;
            float Ang(Quaternion q0, Transform x) { (Quaternion.Inverse(q0) * x.localRotation).ToAngleAxis(out var a, out var ax); return a * Mathf.Sign(ax.x); }
            var hj = _robot.GetComponentsInChildren<Joint>().Where(h => h.transform == head || h.connectedBody && h.connectedBody.transform == head).ToList();
            for (var f = 0; f < 20; f++)
            {
                L($"deploy t={f * 0.1f:F1}: bar={Ang(r0[0], barT):F1} top={Ang(r0[1], top):F1} head={Ang(r0[2], head):F1} headMoved={(head.localPosition - p0).magnitude * 1000:F0}mm " +
                  $"{Loop()} hingeForce={string.Join("/", hj.Select(h => h.currentForce.magnitude.ToString("F0")))} barW={barT.GetComponent<Rigidbody>().angularVelocity.x:F2} " +
                  $"target={barT.GetComponent<ConfigurableJoint>().targetAngularVelocity.x:F2}");
                yield return new WaitForSeconds(0.1f);
            }
        }
        L($"intake out: {Status()} {Loop()} | {Kick()}");
        L($"intake contacts: {string.Join(" | ", contacts.Distinct().Take(20))}");
        var outOk = Mathf.Abs(IntakeAngle()) < 6f;
        var headLow = head.position.y - _robot.transform.position.y;
        Release(Key.LeftShift);
        for (var f = 0; f < 20; f++)
        {
            L($"return t={f * 0.1f:F1}: intake={IntakeAngle():F1} {Loop()} | {Kick()}");
            yield return new WaitForSeconds(0.1f);
        }
        L($"intake back: {Status()} {Loop()}");
        Result("intake four-bar", outOk && IntakeAngle() > 70f, $"deployed ok={outOk} head height deployed={headLow:F3} | {Loop()}");
    }

    // ---------------------------------------------------------------- pieces into the robot

    // Floor coral: lying across the robot's path 1.1 m behind it (the intake side), the robot backs into it.
    IEnumerator GetFloorCoral(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        Put(coral, t.position - t.forward * 1.1f + Vector3.up * 0.06f, Quaternion.AngleAxis(_floorYaw, Vector3.up) * Along(coral, t.right));
        AnemoneCoralTrack track = null;
        if (_trackFloor)
        {
            track = coral.gameObject.AddComponent<AnemoneCoralTrack>();
            track.robot = t;
            track.label = label;
            L($"{label}: start y {coral.transform.position.y:F3} long(robot) {t.InverseTransformDirection(coral.transform.forward):F2} carpet top {(GameObject.Find("CarpetCollider") ? GameObject.Find("CarpetCollider").GetComponent<Collider>().bounds.max.y : 0f):F3} size {(GameObject.Find("CarpetCollider") ? GameObject.Find("CarpetCollider").GetComponent<Collider>().bounds.size.y : 0f):F3}");
        }
        yield return new WaitForSeconds(0.5f);
        if (label == "floor coral")
        {
            StartVideo("indexing_side", new Vector3(0, 0.32f, -0.30f), new Vector3(1, 0.15f, -0.05f), 1.3f);
            StartVideo("indexing_top", new Vector3(0, 0.30f, -0.25f), new Vector3(0.45f, 1f, -0.55f), 1.4f);
            if (Environment.GetEnvironmentVariable("ANEMONE_VIDEO_MORE") == "1")
            {
                StartVideo("indexing_back", new Vector3(0, 0.25f, -0.35f), new Vector3(0.2f, 0.5f, -1f), 1.3f);
                StartVideo("indexing_close", new Vector3(0, 0.30f, -0.22f), new Vector3(0.2f, 1f, -0.25f), 0.8f);
                StartVideo("indexing_left", new Vector3(0, 0.32f, -0.30f), new Vector3(-1, 0.15f, -0.05f), 1.3f);
            }
        }
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(1.5f);
        var end = Time.time + 5f;
        var logAt = 0f;
        while (Time.time < end && !(HasCoral && NodeOf("Coral").atTarget))
        {
            if (!HasCoral) yield return Drive(-t.forward, 0.25f);
            else
                for (var k = 0; k < 5; k++)
                {
                    ShotAt(label.Replace(' ', '_') + "_path", new Vector3(0, 0.35f, -0.2f), new Vector3(1, 0.1f, 0), 1.0f);
                    yield return new WaitForSeconds(0.05f);
                }
            if (Time.time > logAt)
            {
                logAt = Time.time + 0.5f;
                L($"{label} … {Status()} coral={Local(coral.transform.position)} state={NodeOf("Coral").currentStateNum}/{NodeOf("Coral").atTarget}");
                // what the coral is up against, and whether the roller strip sees it
                var strip = Find("Ground Coral Intake").GetComponent<BoxCollider>();
                var seen = Physics.OverlapBox(strip.transform.position, Vector3.Scale(strip.size, strip.transform.lossyScale) / 2,
                    strip.transform.rotation, LayerMask.GetMask("Coral")).Any(c => c.GetComponentInParent<PieceCtrl>() == coral);
                var touching = Physics.OverlapSphere(coral.transform.position, 0.075f, LayerMask.GetMask("Robot"), QueryTriggerInteraction.Ignore)
                    .Where(c => c.transform.IsChildOf(_robot.transform)).Select(c => c.name).Distinct().Take(8);
                L($"   strip={Local(strip.transform.position)} sees={seen} near: {string.Join(", ", touching)}");
            }
        }
        yield return new WaitForSeconds(1f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1.5f);
        StopVideos();
        if (track) Object.Destroy(track);
        Result(label, HasCoral && NodeOf("Coral").atTarget, $"{Status()} coral={Local(coral.transform.position)} state={Local(Find("Coral Tube State").position)}");
    }

    // HP station: the front (tube top) toward the chute, holding intake until the station drops.
    IEnumerator GetStationCoral(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Station);
        var station = Object.FindObjectsByType<CoralStation>(FindObjectsSortMode.None).First(s => s.Alliance == _robot.Alliance);
        var spawn = (Transform)typeof(CoralStation).GetField("coralSpawnpoint", F).GetValue(station);
        var sf = spawn.forward;
        sf.y = 0;
        sf.Normalize();
        PlaceRobot(spawn.position + sf * 0.75f, -sf);
        yield return new WaitForSeconds(0.5f);
        Hold(Key.LeftShift);
        var end = Time.time + 8f;
        var top = Find("Coral Eject");
        while (Time.time < end && !HasCoral)
        {
            yield return new WaitForSeconds(0.25f);
            var c = FreePiece("Coral", top.position);
            L($"{label} … {Status()} dropDist={_robot.CurrentCoralStationMode.DropDistance:F2} " +
              $"nearest coral={(c ? Local(c.transform.position) : "-")} v={(c ? c.GetComponent<Rigidbody>().velocity.magnitude : 0):F2} " +
              (c ? $"from lip: along {top.InverseTransformPoint(c.transform.position).z:F3} off axis {new Vector2(top.InverseTransformPoint(c.transform.position).x, top.InverseTransformPoint(c.transform.position).y).magnitude:F3} angle {Vector3.Angle(c.transform.forward, top.forward):F0}" : ""));
        }
        yield return new WaitForSeconds(1f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, HasCoral, $"{Status()} spawn(robot)={Local(spawn.position)} tubeTop={Local(Find("Coral Eject").position)}");
        if (HasCoral)
        {
            // is the held coral in the middle of the tube? its mesh centre against the tube axis (Coral Eject points up it)
            var eject = Find("Coral Eject");
            var piece = NodeOf("Coral").controller.transform;
            var mb = piece.GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; });
            // (mesh data isn't readable at run time: the render bounds, the coral's mesh centred on its own axis)
            var centre = mb.center;
            var off = Vector3.ProjectOnPlane(centre - eject.position, eject.forward);
            var topEnd = Vector3.Dot(centre - eject.position, eject.forward) + 0.15f;
            L($"held coral: centre {off.magnitude * 1000:F1} mm off the tube axis (robot dir {_robot.transform.InverseTransformDirection(off.normalized):F2}), " +
              $"top end {topEnd * 1000:F0} mm from the lip, bounds {mb.size:F2}");
            var c = _robot.transform.InverseTransformPoint(NodeOf("Coral").controller.transform.position);
            ShotAt("held_coral", c, new Vector3(1, 0.2f, 0), 1.0f);
            ShotAt("held_coral", c, new Vector3(0.4f, 0.3f, 1), 1.0f);
            ShotAt("held_coral", c, new Vector3(-0.5f, 0.2f, -1), 1.0f);
        }
    }

    AlignNode Face(int i) => Object.FindObjectsByType<AlignNode>(FindObjectsSortMode.None)
        .Where(n => n.GetComponentInParent<Reef>() && n.GetComponentInParent<Reef>().Alliance == _robot.Alliance)
        .OrderBy(n => n.transform.position.x).ThenBy(n => n.transform.position.z).ElementAt(i);

    Vector3 ReefCentre(AlignNode face) => face.GetComponentInParent<Reef>().transform.position;

    // ---------------------------------------------------------------- scoring

    // Coral on the reef: 1.3 m out facing the face, the level, auto-align (it drives in), then place.
    // A reef face whose algae does not block the level (low algae blocks L2, high blocks L3, as in the real game).
    AlignNode ClearFace(string level, int preferred)
    {
        var algae = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Algae").ToList();
        bool Blocked(AlignNode n) => algae.Any(a =>
            Vector2.Distance(new Vector2(a.transform.position.x, a.transform.position.z), new Vector2(n.transform.position.x, n.transform.position.z)) < 0.9f &&
            (level == "L2" ? a.transform.position.y is > 0.6f and < 1.1f : level == "L3" && a.transform.position.y is > 1.1f and < 1.6f));
        for (var k = 0; k < 6; k++)
        {
            var n = Face((preferred + k) % 6);
            if (!Blocked(n)) return n;
        }
        return Face(preferred);
    }

    // Coral left on this face by earlier trials (on branches, in the trough, at its foot) goes off the field, so each trial
    // starts from an empty face.
    void ClearFaceCoral(AlignNode f)
    {
        var held = NodeOf("Coral").controller;
        foreach (var c in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral" && p != held))
        {
            var cp = c.transform.position;
            if (Vector2.Distance(new Vector2(cp.x, cp.z), new Vector2(f.transform.position.x, f.transform.position.z)) > 1.4f) continue;
            L($"clearing coral at {cp:F2} off the face");
            Put(c, new Vector3(-6f + 0.4f * _parked++, 0.3f, -3.5f), Quaternion.identity);
        }
    }
    int _parked;

    IEnumerator ScoreCoral(string level, Key key, int face)
    {
        var f = ClearFace(level, face);
        ClearFaceCoral(f);
        // a driver descores the algae that blocks the level first: move it out of the way
        foreach (var a in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Algae"))
        {
            var ap = a.transform.position;
            var flat = Vector2.Distance(new Vector2(ap.x, ap.z), new Vector2(f.transform.position.x, f.transform.position.z));
            if (flat < 1.2f && (level == "L2" ? ap.y is > 0.6f and < 1.1f : level == "L3" && ap.y is > 1.1f and < 1.6f))
            {
                L($"score {level}: moving blocking algae at {ap:F2} off the reef");
                Put(a, new Vector3(-6f, 0.3f, -2.5f), Quaternion.identity);
            }
        }
        var toReef = ReefCentre(f) - f.transform.position;
        toReef.y = 0;
        toReef.Normalize();
        PlaceRobot(f.transform.position - toReef * 1.3f, toReef);
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        var bBranch = ScoredBranches(level == "L1" ? null : level);
        yield return Tap(key);
        yield return new WaitForSeconds(2.5f);
        Hold(Key.Q);
        yield return new WaitForSeconds(3f);
        var piece = NodeOf("Coral").controller;
        var br = Object.FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None)
            .SelectMany(p => ((Array)Branches.GetValue(p)).Cast<object>())
            .Where(b => level == "L1" || b.GetType().GetProperty("TargetHeight").GetValue(b).ToString() == level)
            .OrderBy(b => ((Vector3)b.GetType().GetField("center").GetValue(b) - (piece ? piece.transform.position : Vector3.zero)).sqrMagnitude).First();
        var bc = (Vector3)br.GetType().GetField("center").GetValue(br);
        var bq = (Quaternion)br.GetType().GetField("rotation").GetValue(br);
        string Rel() => piece ? (Quaternion.Inverse(bq) * (piece.transform.position - bc)).ToString("F2") : "-";
        L($"score {level} branch axes(robot): fwd={_robot.transform.InverseTransformDirection(bq * Vector3.forward):F2} up={_robot.transform.InverseTransformDirection(bq * Vector3.up):F2} right={_robot.transform.InverseTransformDirection(bq * Vector3.right):F2} bounds={br.GetType().GetField("bounds").GetValue(br)}");
        if (level == "L1")
            foreach (var tr in Object.FindObjectsByType<TroughScorer>(FindObjectsSortMode.None))
            foreach (var c in (BoxCollider[])typeof(TroughScorer).GetField("troughTriggers", F).GetValue(tr))
                if (c && (c.bounds.center - _robot.transform.position).magnitude < 2f)
                    L($"score L1 trough trigger centre(robot)={Local(c.bounds.center)} size={c.bounds.size:F2}");
        L($"score {level} distance to the face node: {Vector3.ProjectOnPlane(f.transform.position - _robot.transform.position, Vector3.up).magnitude:F3} m");
        L($"score {level} at reef: {Status()} coral(robot)={(piece ? Local(piece.transform.position) : "-")} tubeTop={Local(Find("Coral Eject").position)} branch(robot)={Local(bc)} coralInBranch={Rel()}");
        var shots = Environment.GetEnvironmentVariable("ANEMONE_SHOTS");
        if (!string.IsNullOrEmpty(shots) && !_cam)
        {
            _cam = new GameObject("ShotCam").AddComponent<Camera>();
            _cam.enabled = false;
            _cam.fieldOfView = 35;
            _rt = new RenderTexture(960, 720, 24);
            _cam.targetTexture = _rt;
        }
        var dir = string.IsNullOrEmpty(shots) ? null : System.IO.Path.Combine(shots, "score_" + level);
        if (_cam)
        {
            // side-on, from the robot's right, framing the tube top and the branch
            var mid = (Find("Coral Eject").position + bc) / 2;
            _cam.transform.position = mid + _robot.transform.right * 1.6f + Vector3.up * 0.15f;
            _cam.transform.LookAt(mid);
        }
        var frame = 0;
        if (dir != null) Shot(dir, frame++);
        // L1: what the coral touches on its way out
        var coralHits = new List<string>();
        var coralLog = piece ? piece.gameObject.AddComponent<AnemoneContactLog>() : null;
        if (coralLog) { coralLog.sink = coralHits; coralLog.frame = _robot.transform; }
        var pressedAt = Time.time;
        yield return Tap(Key.K);
        if (dir != null)
            for (var k = 0; k < 12; k++) { yield return new WaitForSeconds(0.05f); Shot(dir, frame++); }
        for (var k = 0; k < 6; k++)
        {
            yield return new WaitForSeconds(0.5f);
            if (dir != null) Shot(dir, frame++);
            L($"score {level} +{(k + 1) * 0.5f:F1}s coralInBranch={Rel()} coral(robot)={(piece ? Local(piece.transform.position) : "-")} axis={(piece ? _robot.transform.InverseTransformDirection(piece.transform.up).ToString("F2") : "-")} branchesScored={ScoredBranches()} {Status()}");
        }
        Release(Key.Q);
        if (coralLog)
        {
            L($"score {level} pressed at {pressedAt:F2}s; chute wall contacts {coralHits.Count(h => h.Contains("Chute Wall"))}; coral touched: {string.Join(" | ", coralHits.Where(h => !h.Contains("Chute Wall")).Take(5))}");
            Object.Destroy(coralLog);
        }
        var after = Score();
        if (level == "L1")
            // the trough scorer adds into CoralPoints (TroughPoints is never set by the game)
            Result($"score {level}", after.CoralPoints > before.CoralPoints && ScoredBranches() == bBranch, $"coral points {before.CoralPoints}->{after.CoralPoints} | {Pts(after)}");
        else
            Result($"score {level}", ScoredBranches(level) > bBranch, $"{level} branches {bBranch}->{ScoredBranches(level)} | {Pts(after)}");
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1f);
    }

    float _floorYaw;
    bool _trackFloor;

    // ANEMONE_ONLY=floorangles: floor coral at many angles, tracked ("ANEMONE_YAWS=a,b,.." to choose)
    IEnumerator FloorAngles()
    {
        _trackFloor = true;
        var yaws = (Environment.GetEnvironmentVariable("ANEMONE_YAWS") ?? "-40,-40,-60,-25,25,40,60").Split(',').Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture));
        var i = 0;
        foreach (var yaw in yaws)
        {
            _floorYaw = yaw;
            // the last coral off the floor here
            foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral" && !p.transform.IsChildOf(_robot.transform)))
                if (Environment.GetEnvironmentVariable("ANEMONE_KEEPFLOOR") != "1" && Vector3.Distance(p.transform.position, Open) < 3f)
                    Put(p, new Vector3(-7f + 0.4f * i++, 0.1f, -3f), Quaternion.identity);
            yield return GetFloorCoral($"floor angle {yaw:F0}");
            if (HasCoral) { yield return Tap(Key.K); yield return new WaitForSeconds(1.5f); }
            var strays = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral" && !p.transform.IsChildOf(_robot.transform) && Vector3.Distance(p.transform.position, Open) < 3f)
                .Select(p => $"{p.transform.position - Open:F2}");
            L($"floor angle {yaw:F0}: corals near afterwards {string.Join(" ", strays)}");
        }
        _floorYaw = 0f;
        _trackFloor = false;
    }

    // ANEMONE_ONLY=tube: everything that moves a coral near the tube, with the wall watcher on
    IEnumerator TubeWatch()
    {
        var bore = Find("Chute Walls");
        var watch = new GameObject("Tube Watch").AddComponent<AnemoneTubeWatch>();
        watch.bore = bore;
        watch.walls = bore.GetComponentsInChildren<Collider>(true);
        watch.status = Status;
        L($"tube watch: {watch.walls.Length} wall boxes");
        _trackFloor = true;
        foreach (var yaw in (Environment.GetEnvironmentVariable("ANEMONE_YAWS") ?? "0,40,-40,90").Split(',').Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)))
        {
            _floorYaw = yaw;
            var below = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral" && p.transform.position.y < 0.02f).Select(p => $"{p.transform.position:F2}");
            L($"before yaw {yaw:F0}: coral below the carpet: {string.Join(" ", below)}");
            watch.phase = $"floor intake yaw {yaw:F0}";
            yield return GetFloorCoral($"floor coral yaw {yaw:F0}");
            // with it seated: the arm swinging to every level and back, the robot driving hard
            watch.phase = $"held swings after yaw {yaw:F0}";
            if (HasCoral)
                foreach (var (key, drive) in new[] { (Key.P, Key.W), (Key.Z, Key.D), (Key.O, Key.S), (Key.I, Key.A), (Key.U, Key.W), (Key.Z, Key.S) })
                {
                    yield return Tap(key);
                    Hold(drive);
                    yield return new WaitForSeconds(0.7f);
                    Release(drive);
                    yield return new WaitForSeconds(0.5f);
                }
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1f);
            if (HasCoral) { yield return Tap(Key.K); yield return new WaitForSeconds(1.5f); }
        }
        _floorYaw = 0f;
        _trackFloor = false;
        if (Environment.GetEnvironmentVariable("ANEMONE_YAWS") != null) { Object.Destroy(watch.gameObject); yield break; }
        for (var i = 0; i < 6; i++)
        {
            // the corals from the last try off the floor here: the robot is put back on this spot
            var moved = 0;
            foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral" && !p.transform.IsChildOf(_robot.transform)))
                if (Vector3.Distance(p.transform.position, _robot.transform.position) < 3f)
                    Put(p, new Vector3(-7f + 0.4f * moved++, 0.1f, -3f), Quaternion.identity);
            yield return new WaitForSeconds(0.3f);
            watch.phase = "station";
            var seen = new List<PieceCtrl>(Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None));
            yield return GetStationCoral($"station coral {i + 1}");
            foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Coral" && !seen.Contains(p)))
                L($"station coral {i + 1} new coral now at robot {Local(p.transform.position)} up={_robot.transform.InverseTransformDirection(p.transform.forward):F2} tube top {Local(Find("Coral Eject").position)}");
            yield return new WaitForSeconds(1f);
            if (HasCoral)
            {
                watch.phase = "station held";
                yield return Tap(Key.Z);
                yield return new WaitForSeconds(1f);
                // clear of the station, and of any coral it dropped on the tube, before letting this one go
                PlaceRobot(new Vector3(-5f, 0f, 2f), Vector3.forward);
                yield return new WaitForSeconds(0.5f);
                watch.phase = "station coral out";
                yield return Tap(Key.K);
                yield return new WaitForSeconds(1.5f);
            }
        }
        foreach (var (lvl, key, face) in new[] { ("L4", Key.P, 0), ("L3", Key.O, 1), ("L1", Key.U, 3) })
        {
            watch.phase = $"floor for {lvl}";
            yield return GetFloorCoral($"floor coral for {lvl}");
            watch.phase = $"score {lvl}";
            if (HasCoral) yield return ScoreCoral(lvl, key, face);
        }
        foreach (var kv in watch.phases)
            L($"tube {kv.Key}: deepest {kv.Value.max * 100:F1} cm, frames over 1 cm {kv.Value.deep}/{kv.Value.frames}; {kv.Value.worst}");
        Object.Destroy(watch.gameObject);
    }

    // ANEMONE_ONLY=look: the intake side-on, stowed and deployed, and where the kicker is
    IEnumerator Look()
    {
        var dir = Environment.GetEnvironmentVariable("ANEMONE_SHOTS");
        _cam = new GameObject("ShotCam").AddComponent<Camera>();
        _cam.enabled = false;
        _cam.fieldOfView = 30;
        _rt = new RenderTexture(1100, 800, 24);
        _cam.targetTexture = _rt;
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(new Vector3(-6f, 0f, -2.5f), Vector3.forward);
        yield return new WaitForSeconds(1.5f);
        void Pose(string tag)
        {
            var kicker = Find("Kicker");
            L($"look {tag}: {Status()} kicker(robot)={Local(kicker.position)} kickerUp={_robot.transform.InverseTransformDirection(kicker.up):F2} rollers: " +
              string.Join(" ", kicker.GetComponentsInChildren<Collider>(true).Select(c => $"{c.name}{Local(c.bounds.center)}")));
            var aim = kicker.position - Vector3.up * 0.1f;
            foreach (var (name, off) in new[] { ("side", _robot.transform.right * 1.1f), ("rear", -_robot.transform.forward * 1f + _robot.transform.right * 0.5f + Vector3.up * 0.3f) })
            {
                _cam.transform.position = aim + off;
                _cam.transform.LookAt(aim);
                Shot(System.IO.Path.Combine(dir, tag + "_" + name), 0);
            }
        }
        Pose("stow");
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        Pose("deployed");
        // the deployed head's rollers and the coral's path past them, side-on (y up, z toward the robot's front)
        foreach (var c in _robot.GetComponentsInChildren<CapsuleCollider>(true).Where(c => c.name.EndsWith(" Roller") || c.name.Contains("Shaft")))
            L($"roller {c.name} parent={c.transform.parent.name} at {Local(c.bounds.center)} r={c.radius * c.transform.lossyScale.y:F3}");
        L($"kicker roller side-on: {Local(Find("Kicker").position)}");
        foreach (var n in new[] { "Intake", "Pinch", "Kick", "Under", "Over", "Entry", "Channel" })
            L($"path {n} at {Local(Find($"Coral {n} State").position)}");
        Release(Key.LeftShift);
    }

    // what on the robot a held ball is inside (held pieces have no collisions of their own)
    string AlgaeOverlaps(PieceCtrl piece)
    {
        var ball = piece.GetComponentsInChildren<SphereCollider>(true).FirstOrDefault();
        if (!ball) return "no sphere";
        var layer = ball.gameObject.layer;
        return string.Join(", ", _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled && !c.transform.IsChildOf(piece.transform) &&
                (c.excludeLayers & (1 << layer)) == 0 && !Physics.GetIgnoreLayerCollision(c.gameObject.layer, layer) &&
                Physics.ComputePenetration(ball, ball.transform.TransformPoint(ball.center), ball.transform.rotation, c, c.transform.position, c.transform.rotation, out _, out var d) && d > 0.003f)
            .Select(c => c.name).Distinct().Take(10));
    }

    // Reef algae: 1.3 m out from the face with the algae, the level by its height, hold intake and drive in.
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
        ClearFaceCoral(pick.face);
        PlaceRobot(pick.face.transform.position - toReef * 1.3f, toReef);
        yield return new WaitForSeconds(0.5f);
        yield return Tap(high ? Key.O : Key.I);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        L($"{label} before approach: {Status()} algae(robot)={Local(pick.p.transform.position)} target={Local(Find("Algae State").position)}");
        var touches = new List<string>();
        foreach (var rb in _robot.GetComponentsInChildren<Rigidbody>())
            rb.gameObject.AddComponent<AnemoneContactLog>().sink = touches;
        Hold(Key.Q);
        var end = Time.time + 4f;
        while (Time.time < end && !HasAlgae) yield return new WaitForSeconds(0.2f);
        var mid = (_robot.transform.InverseTransformPoint(pick.p.transform.position) + _robot.transform.InverseTransformPoint(Find("Algae State").position)) / 2;
        ShotAt(label.Replace(' ', '_') + "_reach", mid, new Vector3(1, 0.05f, 0), 1.6f);
        ShotAt(label.Replace(' ', '_') + "_reach", mid, new Vector3(0.7f, 0.3f, -0.6f), 1.6f);
        var bc = pick.p.transform.position;
        var br = pick.p.GetComponentInChildren<SphereCollider>() is { } sc ? sc.radius * sc.transform.lossyScale.x : 0.2f;
        L($"{label} wheel gaps: " + string.Join(", ", _robot.GetComponentsInChildren<CapsuleCollider>().Where(c => c.name.EndsWith(" Wheel"))
            .Select(c => $"{c.name} at {Local(c.transform.position)} gap {((c.ClosestPoint(bc) - bc).magnitude - br) * 100:F1} cm")) +
          $" | nearest robot part: " + string.Join(", ", _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled)
            .Select(c => (c, d: (c.ClosestPoint(bc) - bc).magnitude - br)).OrderBy(x => x.d).Take(3).Select(x => $"{x.c.transform.parent.name}/{x.c.name} {x.d * 100:F1} cm")));
        L($"{label} at reef: {Status()} algae(robot)={Local(pick.p.transform.position)} target={Local(Find("Algae State").position)} " +
          $"touching: {string.Join(" | ", touches.Where(t => !t.Contains("[Robot]")).Distinct().Take(12))}");
        foreach (var cl in _robot.GetComponentsInChildren<AnemoneContactLog>()) Object.Destroy(cl);
        yield return new WaitForSeconds(0.5f);
        Release(Key.Q, Key.LeftShift);
        yield return Drive(-toReef, 0.8f);
        yield return new WaitForSeconds(1f);
        L($"{label} held overlaps: {AlgaeOverlaps(pick.p)}");
        Result(label, HasAlgae, $"{Status()} algae(robot)={Local(pick.p.transform.position)}");
        if (HasAlgae)
        {
            yield return new WaitForSeconds(1f);
            var a = _robot.transform.InverseTransformPoint(NodeOf("Algae").controller.transform.position);
            ShotAt("held_algae", a, new Vector3(1, 0.2f, 0), 1.4f);
            ShotAt("held_algae", a, new Vector3(0.4f, 0.4f, 1), 1.4f);
        }
    }

    // Floor algae: ball 1.0 m in front; algae mode, hold intake, drive into it.
    // A coral standing on end (as on the lollipop marks): driving into it with the intake out must knock it over first,
    // not take it standing; once it lies on the carpet it is taken.
    IEnumerator Lollipop()
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Ground);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        var longLocal = (Vector3)_robot.GetType().GetField("coralLongLocal", F).GetValue(_robot);
        Put(coral, t.position - t.forward * 0.95f + Vector3.up * 0.16f, Quaternion.FromToRotation(longLocal, Vector3.up));
        yield return new WaitForSeconds(0.8f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(1.5f);
        var takenStanding = false;
        var end = Time.time + 6f;
        // judged on the last look before it was taken (once taken it turns end-first up the indexer)
        var prevUp = Mathf.Abs(Vector3.Dot(coral.transform.TransformDirection(longLocal), Vector3.up));
        while (Time.time < end && !HasCoral)
        {
            yield return Drive(-t.forward, 0.2f);
            var up = Mathf.Abs(Vector3.Dot(coral.transform.TransformDirection(longLocal), Vector3.up));
            if (HasCoral && prevUp > 0.7f) takenStanding = true;
            prevUp = up;
            L($"lollipop … standing={up:F2} coral={Local(coral.transform.position)} taken={HasCoral}");
        }
        yield return new WaitForSeconds(1.5f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result("lollipop knocked down then taken", HasCoral && !takenStanding, $"{Status()} takenStanding={takenStanding}");
    }

    // 180 carries both: a floor coral, then intake again for a floor algae; score the coral with the algae held,
    // then the algae with nothing else
    IEnumerator Both()
    {
        yield return GetFloorCoral("both: floor coral");
        if (!HasCoral) { Result("both: algae with coral", false, "no coral"); yield break; }
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        // a lollipop (180 takes algae off the ground only from a coral stack)
        var stand = FreePiece("Coral", t.position + Vector3.right * 3f);
        var longLocal = (Vector3)_robot.GetType().GetField("coralLongLocal", F).GetValue(_robot);
        Put(stand, t.position + t.forward * 1.0f + Vector3.up * 0.16f, Quaternion.FromToRotation(longLocal, Vector3.up));
        var algae = FreePiece("Algae", t.position);
        Put(algae, t.position + t.forward * 1.0f + Vector3.up * 0.50f, Quaternion.identity);
        yield return new WaitForSeconds(1f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        var end = Time.time + 5f;
        while (Time.time < end && !HasAlgae)
            yield return Drive(t.forward, 0.15f);
        yield return new WaitForSeconds(1f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1.5f);
        Result("both: algae with coral", HasAlgae && HasCoral, $"{Status()} algae(robot)={Local(algae.transform.position)}");
        if (!(HasAlgae && HasCoral)) yield break;
        // the algae first, with the coral still in the tube; then the coral
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
        yield return ScoreNet();
        Result("both: coral kept after scoring algae", HasCoral && !HasAlgae, Status());
        if (!HasCoral) yield break;
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Coral) yield return Tap(Key.Semicolon);
        yield return ScoreCoral("L2", Key.I, 2);
    }

    IEnumerator GetFloorAlgae(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Algae, DropType.Station);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var algae = FreePiece("Algae", t.position);
        Put(algae, t.position + t.forward * 1.0f + Vector3.up * 0.25f, Quaternion.identity);
        yield return new WaitForSeconds(1f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        L($"{label} before approach: {Status()} algae(robot)={Local(algae.transform.position)} target={Local(Find("Algae State").position)}");
        var end = Time.time + 4f;
        while (Time.time < end && !HasAlgae)
            yield return Drive(t.forward, 0.25f);
        yield return new WaitForSeconds(1f);
        Release(Key.LeftShift);
        yield return new WaitForSeconds(1f);
        Result(label, HasAlgae, $"{Status()} algae(robot)={Local(algae.transform.position)} target={Local(Find("Algae State").position)}");
    }

    // Lollipop algae: a ball on a coral standing on end, 1.0 m in front. 180's LOLLIPOP_INTAKE (in algae mode, at stow,
    // the L1 key picks Stack), hold intake and drive in: taken once the bottom wheel touches the ball.
    IEnumerator GetLollipopAlgae(string label)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Algae, DropType.Station);
        PlaceRobot(Open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var t = _robot.transform;
        var coral = FreePiece("Coral", t.position);
        var longLocal = (Vector3)_robot.GetType().GetField("coralLongLocal", F).GetValue(_robot);
        Put(coral, t.position + t.forward * 1.0f + Vector3.up * 0.16f, Quaternion.FromToRotation(longLocal, Vector3.up));
        var algae = FreePiece("Algae", t.position);
        Put(algae, t.position + t.forward * 1.0f + Vector3.up * 0.50f, Quaternion.identity);
        yield return new WaitForSeconds(1f);
        yield return Tap(Key.U);
        yield return new WaitForSeconds(1.5f);
        Hold(Key.LeftShift);
        L($"{label} before approach: {Status()} algae(robot)={Local(algae.transform.position)} target={Local(Find("Algae State").position)}");
        var end = Time.time + 5f;
        while (Time.time < end && !HasAlgae)
            yield return Drive(t.forward, 0.15f);
        L($"{label} at ball: {Status()} algae(robot)={Local(algae.transform.position)}");
        yield return new WaitForSeconds(1f);
        Release(Key.LeftShift);
        yield return Tap(Key.U);
        yield return new WaitForSeconds(1.5f);
        Result(label, HasAlgae, $"{Status()} algae(robot)={Local(algae.transform.position)} target={Local(Find("Algae State").position)}");
    }

    IEnumerator ScoreProcessor()
    {
        var proc = Object.FindObjectsByType<ProcessorScorer>(FindObjectsSortMode.None).First(p => p.Alliance == _robot.Alliance);
        var trig = (BoxCollider)typeof(ProcessorScorer).GetField("scoreTrigger", F).GetValue(proc);
        var target = trig ? trig.bounds.center : proc.transform.position;
        var wall = new Vector3(0, 0, Mathf.Sign(target.z));
        // the ball rides off the robot's centre line (the algae wheels are to the left): line the ball up, not the robot
        var opening = proc.transform.position;
        PlaceRobot(new Vector3(opening.x, 0, target.z) - wall * 1.6f, wall);
        var off = Vector3.Dot(Find("Algae State").position - _robot.transform.position, _robot.transform.right);
        PlaceRobot(_robot.transform.position - _robot.transform.right * off, wall);
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        yield return Tap(Key.U);
        yield return new WaitForSeconds(2f);
        yield return Drive(wall, 1.5f);
        L($"processor approach: {Status()} trigger(robot)={Local(target)} algae(robot)={(NodeOf("Algae").controller ? Local(NodeOf("Algae").controller.transform.position) : "-")}");
        yield return Tap(Key.K);
        yield return new WaitForSeconds(3.5f);
        var after = Score();
        Result("score processor", after.TotalPoints > before.TotalPoints, $"{Pts(before)} -> {Pts(after)}");
    }

    IEnumerator ScoreNet()
    {
        var barge = Object.FindObjectsByType<BargeScorer>(FindObjectsSortMode.None).First(b => b.Alliance == _robot.Alliance);
        var box = barge.GetComponent<BoxCollider>();
        var target = box ? box.bounds.center : barge.transform.position;
        var ourSide = Mathf.Sign(Object.FindObjectsByType<Reef>(FindObjectsSortMode.None).First(r => r.Alliance == _robot.Alliance).transform.position.x);
        var toNet = new Vector3(-ourSide, 0, 0);
        PlaceRobot(new Vector3(ourSide * 1.6f, 0, target.z), toNet);
        yield return new WaitForSeconds(0.5f);
        var before = Score();
        StartVideo("barge", new Vector3(0, 1.5f, 0.6f), new Vector3(0.35f, 0.45f, -1f), 3.2f);
        yield return Tap(Key.P);
        yield return new WaitForSeconds(3f);
        // Barge: the ball carried at ~1 m, driving up slowly (0.25x)
        var end = Time.time + 10f;
        // stopped short of the barge (the ball goes up in front of the net, then lobs over it)
        while (Time.time < end && Mathf.Abs(_robot.transform.position.x) > 1.2f)
            yield return Drive(toNet, 0.15f);
        yield return new WaitForSeconds(1f);
        var ball = NodeOf("Algae").controller;
        L($"net: {Status()} net(robot)={Local(target)} netBox={box?.bounds.size:F2} algae(robot)={(ball ? Local(ball.transform.position) : "-")}");
        ShotAt("net_shot", new Vector3(0, 2.0f, 0.4f), new Vector3(1, 0.05f, 0), 3.2f);
        var netTouches = new List<string>();
        foreach (var rb in _robot.GetComponentsInChildren<Rigidbody>())
            rb.gameObject.AddComponent<AnemoneContactLog>().sink = netTouches;
        yield return Tap(Key.K);
        for (var k = 0; k < 32; k++)
        {
            yield return new WaitForSeconds(0.1f);
            ShotAt("net_shot", new Vector3(0, 2.0f, 0.4f), new Vector3(1, 0.05f, 0), 3.2f);
            if (ball) L($"net +{(k + 1) * 0.1f:F1}s ball(robot)={Local(ball.transform.position)} v={ball.GetComponent<Rigidbody>().velocity.magnitude:F2} arm={Arm():F1} overlaps=[{AlgaeOverlaps(ball)}] held={HasAlgae}");
        }
        StopVideos();
        yield return Drive(-toNet, 0.6f);
        yield return new WaitForSeconds(1f);
        var after = Score();
        L($"net contacts: {string.Join(" | ", netTouches.Where(t => !t.Contains("Field]") && !t.Contains("Net")).Distinct().Take(16))}");
        foreach (var cl in _robot.GetComponentsInChildren<AnemoneContactLog>()) Object.Destroy(cl);
        Result("score net", after.NetPoints > before.NetPoints, $"{Pts(before)} -> {Pts(after)}");
        yield return Tap(Key.Z);
    }

    // Deep cage: the climber folds out of the left side; the robot slides left until the arm is between a cage's
    // posts, then climbs. Tried both ways (on past flat, and back up past upright) to see which one lifts.
    IEnumerator Climb()
    {
        // ANEMONE_CLIMB="a,b,..." tries other climbed angles
        var angles = (Environment.GetEnvironmentVariable("ANEMONE_CLIMB") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (angles.Count == 0) angles.Add((float)((Prefabs.Reefscape.Robots.Mods.SPAM._180.AnemoneSetpoint)Field("climbed")).climberAngle);
        foreach (var angle in angles)
            yield return ClimbTry($"climbed at {angle:F0}", angle);
    }

    IEnumerator ClimbTry(string label, float climbedAngle)
    {
        yield return Reset(Games.Reefscape.Enums.ReefscapeRobotMode.Coral, DropType.Station);
        var climbed = (Prefabs.Reefscape.Robots.Mods.SPAM._180.AnemoneSetpoint)Field("climbed");
        var old = climbed.climberAngle;
        climbed.climberAngle = climbedAngle;
        var ourSide = Mathf.Sign(Object.FindObjectsByType<Reef>(FindObjectsSortMode.None).First(r => r.Alliance == _robot.Alliance).transform.position.x);
        var cage = GameObject.FindGameObjectsWithTag("Cage").Where(c => c.GetComponent<Collider>())
            .OrderBy(c => Mathf.Abs(c.transform.position.z)).First();
        var cageRb = cage.GetComponent<Collider>().attachedRigidbody;
        Bounds CageBounds()
        {
            var cs2 = cageRb ? cageRb.GetComponentsInChildren<Collider>() : cage.GetComponents<Collider>();
            var b = cs2[0].bounds;
            foreach (var c in cs2) b.Encapsulate(c.bounds);
            return b;
        }
        var cb = CageBounds();
        L($"climb {label}: cage bounds min={cb.min:F2} max={cb.max:F2}");
        var cs = _robot.GetComponent<ClimbScorer>();
        var away = new Vector3(ourSide, 0, 0);
        var fwd = Vector3.Cross(away, Vector3.up);
        PlaceRobot(new Vector3(cb.center.x + away.x * 1.1f, 0, cb.center.z), fwd);
        yield return new WaitForSeconds(0.5f);
        yield return Tap(Key.C);
        yield return new WaitForSeconds(3f);
        // line the arm up with the cage's middle (between its posts)
        var trig = Find("Cage Trigger");
        var along = Vector3.Dot(CageBounds().center - trig.position, fwd);
        PlaceRobot(_robot.transform.position + fwd * along, fwd);
        yield return new WaitForSeconds(1f);
        L($"climb {label} deployed: {Status()} trigger={Local(trig.position)} cage(robot)={Local(CageBounds().center)}");
        var start = _robot.transform.position;
        // As a driver climbs: slowly sideways with the arm going between two of the cage's uprights, stopping with the
        // bumper just off the cage; then slowly toward the robot's front until an upright leans on the arm.
        var drive = _robot.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
        float NearFace()
        {
            var c = CageBounds();
            return Mathf.Abs(_robot.transform.InverseTransformPoint(c.center).x) - c.extents.x;
        }
        // a steady crawl: the drive's response to its multiplier isn't proportional at low speed, so steer it by the speed
        IEnumerator Creep(Key[] held, Vector3 dir, float speed, Func<bool> done, float timeout)
        {
            var rb = _robot.GetComponent<Rigidbody>();
            Hold(held);
            var mp = 0.1f;
            drive.SetDriveMp(mp);
            var stop = Time.time + timeout;
            while (!done() && Time.time < stop)
            {
                yield return new WaitForFixedUpdate();
                mp = Mathf.Clamp(mp + 0.004f * (speed - Vector3.Dot(rb.velocity, dir)) / speed, 0.02f, 0.6f);
                drive.SetDriveMp(mp);
            }
        }
        var creepKeys = DriveKeys(-away);
        // the near upright has to end inboard of the top lip, so that going forward brings it against the arm bar,
        // between the lip and the lower plate: just off the bumper
        yield return Creep(creepKeys, -away, 0.08f, () => NearFace() <= 0.462f + 0.013f, 20f);
        Release(creepKeys);
        drive.SetDriveMp(1f);
        yield return new WaitForSeconds(0.6f);
        L($"climb {label} arm between the uprights: cage near face {NearFace() * 100:F1} cm from the robot's centre");
        var hooks = Find("Climber").GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
        var cageCols = (cageRb ? cageRb.GetComponentsInChildren<Collider>() : cage.GetComponents<Collider>()).Where(c => !c.isTrigger && !(c is MeshCollider)).ToArray();
        bool Touching() => hooks.Any(h => cageCols.Any(cc => Physics.ComputePenetration(h, h.transform.position, h.transform.rotation, cc, cc.transform.position, cc.transform.rotation, out _, out _) ||
                                                             (cc.ClosestPoint(h.bounds.center) - h.ClosestPoint(cc.bounds.center)).magnitude < 0.005f));
        creepKeys = DriveKeys(fwd);
        var touchedAt = -1f;
        yield return Creep(creepKeys, fwd, 0.1f, () =>
        {
            if (touchedAt < 0 && Touching()) touchedAt = Time.time;
            return touchedAt >= 0 && Time.time - touchedAt > 0.5f; // on a moment, so it leans
        }, 8f);
        var leaning = touchedAt >= 0;
        Release(creepKeys);
        drive.SetDriveMp(1f);
        yield return new WaitForSeconds(0.5f);
        L($"climb {label} upright leaning: {leaning}");
        L($"climb {label} at cage: {Status()} triggered={cs.ScoringTriggered} cage(robot)={Local(CageBounds().center)} trigger={Local(trig.position)}");
        yield return Tap(Key.C);
        var dc = _robot.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
        var lifted = false;
        for (var k = 0; k < 32; k++)
        {
            yield return new WaitForSeconds(0.5f);
            lifted |= cs.ScoringTriggered && !dc.IsTouchingGround;
            L($"climb {label} +{(k + 1) * 0.5f:F1}s {Status()} triggered={cs.ScoringTriggered} touchingGround={dc.IsTouchingGround} cage(robot)={Local(CageBounds().center)}");
        }
        Result($"climb {label}", cs.ScoringTriggered && !dc.IsTouchingGround,
            $"{Status()} triggered={cs.ScoringTriggered} touchingGround={dc.IsTouchingGround} everLifted={lifted}");
        climbed.climberAngle = old;
    }

    [UnityTest]
    [Timeout(2400000)]
    public IEnumerator AnemoneMatch()
    {
        var only = Environment.GetEnvironmentVariable("ANEMONE_ONLY");
        _only = string.IsNullOrEmpty(only) ? null : only.Split(',').ToHashSet();
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
        Assert.AreEqual("Anemone", _robot.GetType().Name, "wrong robot spawned");
        var pi = _robot.GetComponent<PlayerInput>();
        try { pi.SwitchCurrentControlScheme("Robot 1", _kb); } catch (Exception e) { L("scheme " + e.Message); }
        // tuning runs: ANEMONE_SET="field=value,field=value" sets the robot's float fields
        foreach (var kv in (Environment.GetEnvironmentVariable("ANEMONE_SET") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=');
            var val = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            // field=value on the robot, or setpoint.field=value on one of its setpoints
            var path = p[0].Split('.');
            if (path.Length == 2)
            {
                var sp = Field(path[0]);
                sp.GetType().GetField(path[1], F).SetValue(sp, val);
            }
            else _robot.GetType().GetField(p[0], F)?.SetValue(_robot, val);
            L($"set {p[0]} = {val}");
        }
        WatchIntake();
        yield return new WaitForSeconds(1f);
        new GameObject("KeepEnabled").AddComponent<AnemoneKeepEnabled>().timer = Object.FindFirstObjectByType<BaseTimerManager>();
        yield return new WaitForSeconds(2f);

        var robotLayer = LayerMask.NameToLayer("Robot");
        var off = _robot.GetComponentsInChildren<Transform>(true)
            .Where(x => x.gameObject.layer != robotLayer && !x.GetComponentInParent<PieceCtrl>() &&
                        x.gameObject.layer != LayerMask.NameToLayer("Coral") && x.gameObject.layer != LayerMask.NameToLayer("Algae"))
            .Select(x => { var n = x.name + $"[{LayerMask.LayerToName(x.gameObject.layer)}]"; for (var t = x.parent; t && t != _robot.transform; t = t.parent) n = t.name + "/" + n; return n; }).ToList();
        Result("robot layer", off.Count == 0, $"{off.Count} objects off the Robot layer: {string.Join(", ", off.Take(6))}");

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
        // the first seconds after a spawn, and hard driving with the intake going in and out: nothing may throw the robot
        {
            var rb = _robot.GetComponent<Rigidbody>();
            var (maxTilt, maxSpin, maxRise) = (0f, 0f, 0f);
            var groundY = _robot.transform.position.y;
            var keysets = new[] { new[] { Key.W, Key.D }, new[] { Key.S, Key.A }, new[] { Key.A, Key.Q }, new[] { Key.D } };
            for (var k = 0; k < 16; k++)
            {
                if (k % 4 == 0) Hold(Key.LeftShift);
                if (k % 4 == 2) Release(Key.LeftShift);
                Hold(keysets[k % 4]);
                for (var t = Time.time + 0.5f; Time.time < t;)
                {
                    yield return new WaitForFixedUpdate();
                    maxTilt = Mathf.Max(maxTilt, Vector3.Angle(_robot.transform.up, Vector3.up));
                    maxSpin = Mathf.Max(maxSpin, new Vector2(rb.angularVelocity.x, rb.angularVelocity.z).magnitude);
                    maxRise = Mathf.Max(maxRise, _robot.transform.position.y - groundY);
                }
                Release(keysets[k % 4]);
            }
            Release(Key.LeftShift);
            yield return new WaitForSeconds(1f);
            Result("stays on its wheels", maxTilt < 12f && maxRise < 0.1f, $"max tilt {maxTilt:F1} deg, roll/pitch rate {maxSpin:F1} rad/s, rise {maxRise:F3} m; kicker turn on the top bar ok");
        }

        if (_only != null && _only.Contains("look")) yield return Look();
        if (_only != null && _only.Contains("tube")) yield return TubeWatch();
        if (_only != null && _only.Contains("floorangles")) yield return FloorAngles();
        if (Run("mech")) yield return Mechanisms();
        if (Run("floor")) yield return GetFloorCoral("floor coral");
        foreach (var (lvl, key, face) in new[] { ("L4", Key.P, 0), ("L3", Key.O, 1), ("L2", Key.I, 2), ("L1", Key.U, 3) })
        {
            if (!Run(lvl.ToLower())) continue;
            if (!HasCoral) yield return GetFloorCoral($"floor coral for {lvl}");
            if (HasCoral) yield return ScoreCoral(lvl, key, face);
            else Result($"score {lvl}", false, "no coral to score");
        }
        if (Run("station")) yield return GetStationCoral("station coral");
        if (Run("lowalgae")) yield return GetReefAlgae("reef algae low", false);
        if (Run("processor"))
        {
            if (!HasAlgae) yield return GetReefAlgae("reef algae low (processor)", false);
            if (HasAlgae) yield return ScoreProcessor();
        }
        if (Run("highalgae")) yield return GetReefAlgae("reef algae high", true);
        if (Run("net"))
        {
            if (!HasAlgae) yield return GetReefAlgae("reef algae high (net)", true);
            if (HasAlgae) yield return ScoreNet();
        }
        // 180 has no carpet algae pickup (its code: lollipop only); this one runs only when asked for
        if (_only != null && Run("flooralgae")) yield return GetFloorAlgae("floor algae");
        if (Run("lollipopalgae")) yield return GetLollipopAlgae("lollipop algae");
        if (Run("both")) yield return Both();
        if (Run("lollipop")) yield return Lollipop();
        if (Run("climb")) yield return Climb();

        L("SUMMARY\n" + string.Join("\n", _results));
        L($"DONE pass={_results.Count(r => r.Contains(" PASS "))} fail={_results.Count(r => r.Contains(" FAIL "))}");
    }
}
// One camera following the robot (a robot-local point, seen from a robot-local direction), saving every rendered frame.
public class AnemoneRecorder : MonoBehaviour
{
    Camera _cam;
    RenderTexture _rt;
    Texture2D _tex;
    string _dir;
    Transform _robot;
    Vector3 _target, _from;
    float _dist;
    int _n;

    public void Init(string dir, Transform robot, Vector3 target, Vector3 from, float dist)
    {
        _dir = dir; _robot = robot; _target = target; _from = from.normalized; _dist = dist;
        System.IO.Directory.CreateDirectory(dir);
        _cam = gameObject.AddComponent<Camera>();
        _cam.enabled = false;
        _cam.fieldOfView = 40;
        _rt = new RenderTexture(1280, 720, 24);
        _cam.targetTexture = _rt;
        _tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
    }

    void LateUpdate()
    {
        if (!_robot) return;
        var target = _robot.TransformPoint(_target);
        transform.position = target + _robot.TransformDirection(_from) * _dist;
        transform.LookAt(target);
        _cam.Render();
        RenderTexture.active = _rt;
        _tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        RenderTexture.active = null;
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(_dir, $"{_n++:0000}.png"), _tex.EncodeToPNG());
    }

    void OnDestroy()
    {
        if (_rt) _rt.Release();
        if (_tex) Destroy(_tex);
    }
}

public class AnemoneContactLog : MonoBehaviour
{
    public List<string> sink;
    public Transform frame; // contact points are logged in this transform's frame
    void OnCollisionEnter(Collision c)
    {
        foreach (var p in c.contacts)
            sink?.Add($"{Time.time:F2}s {p.otherCollider.transform.parent?.name}/{p.otherCollider.name} v={c.relativeVelocity.magnitude:F1} at {(frame ? frame.InverseTransformPoint(p.point).ToString("F2") : "")}");
    }
    void OnCollisionStay(Collision c)
    {
        foreach (var p in c.contacts)
            sink?.Add($"{p.thisCollider.name}->{p.otherCollider.name}[{LayerMask.LayerToName(p.otherCollider.gameObject.layer)}]");
    }
}
#endif
