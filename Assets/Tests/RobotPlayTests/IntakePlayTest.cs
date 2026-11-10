// Headless play-mode check of a robot's intakes (ground coral, HP coral, algae). Logs "[T]" lines.
//   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter IntakePlayTest -testResults r.xml -logFile l.log
// Robot metadata asset path from the env var TEST_ROBOT (default: Spartans 971).
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
using RobotFramework.Controllers.GamePieceSystem;
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
public class KeepRobotsEnabled : MonoBehaviour
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

public class IntakePlayTest
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    ReefscapeRobotBase _robot;
    Keyboard _kb;
    Key[] _held = Array.Empty<Key>();

    static void L(string s) => Debug.Log("[T] " + s);

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

    Node NodeOf(string piece) =>
        _robot.GetComponent<ReefscapeRobotGamePieceController>().GetPieceByName(piece);

    string Status()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"t={Time.time:F2} sp={_robot.CurrentSetpoint} mode={_robot.CurrentRobotMode} " +
                  $"drop={_robot.CurrentCoralStationMode.DropType} ");
        var el = Field("elevator") as GenericElevator;
        if (el) sb.Append($"elev={el.GetElevatorHeight():F1}in ");
        foreach (var j in new[] { "arm", "wrist", "intake", "climber" })
            if (Field(j) is GenericJoint g)
                sb.Append($"{j}={g.GetSingleAxisAngle(j == "intake" || j == "climber" ? JointAxis.Y : JointAxis.X):F1} ");
        foreach (var n in new[] { "Coral", "Algae" })
        {
            var node = NodeOf(n);
            sb.Append($"| {n} st={node.currentStateNum} to={node.movingTo} was={node.wasMovingTo} at={node.atTarget} ");
        }
        foreach (var i in _robot.GetComponentsInChildren<ReefscapeGamePieceIntake>())
            sb.Append($"| {i.name} req={i.requestIntake} has={i.hasGamePiece} sec={i.securedGamePiece} ");
        return sb.ToString();
    }

    IEnumerator Watch(float seconds, string label, Func<string> extra = null)
    {
        var end = Time.time + seconds;
        while (Time.time < end)
        {
            L($"{label} {Status()} {extra?.Invoke()}");
            yield return new WaitForSeconds(0.5f);
        }
    }

    static readonly FieldInfo Cols = typeof(GamePieceIntake<ReefscapeGamePiece, ReefscapeGamePieceData>)
        .GetField("intakeCollider", F);
    static readonly FieldInfo Target = typeof(GamePieceIntake<ReefscapeGamePiece, ReefscapeGamePieceData>)
        .GetField("target", F);

    static BoxCollider[] Boxes(ReefscapeGamePieceIntake i) => (BoxCollider[])Cols.GetValue(i);

    string Geometry()
    {
        var t = _robot.transform;
        string P(Vector3 w) { var l = t.InverseTransformPoint(w); return $"({l.x:F2},{l.y:F2},{l.z:F2})"; }
        var sb = new System.Text.StringBuilder("robot-local m: ");
        foreach (var i in _robot.GetComponentsInChildren<ReefscapeGamePieceIntake>())
        {
            var tg = (Transform)Target.GetValue(i);
            sb.Append($"| {i.name} boxes=[{string.Join(" ", Boxes(i).Select(b => b ? b.name + P(b.transform.position) + "size" + Vector3.Scale(b.size, b.transform.lossyScale).ToString("F2") : "null"))}] target={(tg ? tg.name + P(tg.position) : "null")} ");
        }
        return sb.ToString();
    }

    PieceCtrl FreePiece(string name, Vector3 near)
    {
        var held = _robot.GetComponentsInChildren<PieceCtrl>().ToHashSet();
        return Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == name && !held.Contains(p) && p.gameObject.activeInHierarchy)
            .OrderBy(p => (p.transform.position - near).sqrMagnitude).FirstOrDefault();
    }

    // A free coral that isn't on the reef, so loading never takes one that has already been scored.
    PieceCtrl SpareCoral(Vector3 near)
    {
        var reef = ReefCentre();
        var held = _robot.GetComponentsInChildren<PieceCtrl>().ToHashSet();
        return Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == "Coral" && !held.Contains(p) && p.gameObject.activeInHierarchy &&
                        Vector3.ProjectOnPlane(p.transform.position - reef, Vector3.up).magnitude > 2.5f)
            .OrderBy(p => (p.transform.position - near).sqrMagnitude).FirstOrDefault() ?? FreePiece("Coral", near);
    }

    // Reef algae blocks a level (low blocks L2, high blocks L3), so the place tests run on a reef without any.
    void ClearReefAlgae()
    {
        var reef = ReefCentre();
        foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None))
            if (p.PieceName == "Algae" && p.transform.position.y > 0.4f &&
                Vector3.ProjectOnPlane(p.transform.position - reef, Vector3.up).magnitude < 1.6f)
                p.gameObject.SetActive(false);
    }

    // Takes coral lying on the carpet around the reef out of the next trial's way.
    void ClearFloorCoral()
    {
        var reef = ReefCentre();
        foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None))
            if (p.PieceName == "Coral" && p.transform.position.y < 0.2f && !p.GetComponentInParent<ReefscapeRobotBase>() &&
                Vector3.ProjectOnPlane(p.transform.position - reef, Vector3.up).magnitude < 2.8f)
                p.gameObject.SetActive(false);
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

    // Rotation that lays a piece's longest local axis along `dir`.
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
            // Clear the dropped piece away so the next intake can't grab it back instead of the one placed for it.
            var held = _robot.GetComponentsInChildren<PieceCtrl>().ToHashSet();
            foreach (var p in Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
                         .Where(p => !held.Contains(p) && p.gameObject.activeInHierarchy && (p.transform.position - _robot.transform.position).magnitude < 1.5f))
                p.gameObject.SetActive(false);
        }
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(2f);
    }

    static readonly string[] Prefs = { "GameMode", "MultiplayerMode", "PerspectiveAutoAlign" };
    readonly System.Collections.Generic.Dictionary<string, int?> _savedPrefs = new();

    [SetUp]
    public void IgnoreGameErrors() => LogAssert.ignoreFailingMessages = true; // the field logs errors on scene changes

    [TearDown]
    public void RestorePrefs()
    {
        foreach (var (k, v) in _savedPrefs)
        {
            if (v.HasValue) PlayerPrefs.SetInt(k, v.Value);
            else PlayerPrefs.DeleteKey(k);
        }
        PlayerPrefs.Save();
    }

    // Loads the robot loader, the Reefscape scene with the test robot, enables it and checks the keyboard reaches it.
    IEnumerator Boot()
    {
        var metaPath = Environment.GetEnvironmentVariable("TEST_ROBOT") ??
                       "Assets/Prefabs/Reefscape/Robots/Mods/Spartans/971/Spartans.asset";
        var meta = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(metaPath);
        Assert.NotNull(meta, metaPath);
        LogAssert.ignoreFailingMessages = true;
        foreach (var k in Prefs) _savedPrefs[k] = PlayerPrefs.HasKey(k) ? PlayerPrefs.GetInt(k) : (int?)null;
        PlayerPrefs.SetInt("GameMode", 0);
        PlayerPrefs.SetInt("MultiplayerMode", 0);
        PlayerPrefs.SetInt("PerspectiveAutoAlign", 0); // reef-relative auto align: no camera needed headless
        InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        InputSystem.settings.editorInputBehaviorInPlayMode =
            InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        _kb = InputSystem.AddDevice<Keyboard>();

        Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/Prefabs/Reefscape/MainMenu/RobotLoader.prefab"));
        var t0 = Time.realtimeSinceStartup;
        yield return new WaitUntil(() => RobotLoader.Instance && RobotLoader.Instance.AllRobotsLoaded ||
                                         Time.realtimeSinceStartup - t0 > 60);

        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (s, m) =>
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
        foreach (var i in _robot.GetComponentsInChildren<ReefscapeGamePieceIntake>(true)) i.debugLog = true;
        var pi = _robot.GetComponent<UnityEngine.InputSystem.PlayerInput>();
        try { pi.SwitchCurrentControlScheme("Robot 1", _kb); } catch (Exception e) { L("scheme " + e.Message); }
        L($"input scheme={pi.currentControlScheme} devices={string.Join(",", pi.devices.Select(d => d.name))}");
        yield return new WaitForSeconds(1f);
        var timer = Object.FindFirstObjectByType<BaseTimerManager>();
        new GameObject("KeepRobotsEnabled").AddComponent<KeepRobotsEnabled>().timer = timer;
        yield return new WaitForSeconds(2f);
        L($"state {MoSimCore.BaseClasses.GameManagement.BaseGameManager.Instance.RobotState}");
        yield return Watch(1f, "start");
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(0.3f);
        L($"input check intakePressed={_robot.IntakeAction.IsPressed()} sp={_robot.CurrentSetpoint}");
        Keys();
        yield return new WaitForSeconds(0.3f);
        if (System.Environment.GetEnvironmentVariable("FIXED_TENSOR") == "1") // compare: joints with a fixed (1,1,1) tensor
            foreach (var rb in _robot.GetComponentsInChildren<Rigidbody>().Where(b => b.GetComponent<ConfigurableJoint>() && b != _robot.GetComponent<Rigidbody>()))
            {
                rb.automaticInertiaTensor = false;
                rb.inertiaTensor = Vector3.one;
                rb.inertiaTensorRotation = Quaternion.identity;
            }
        if (System.Environment.GetEnvironmentVariable("NOPLATES") == "1")
            foreach (var c in _robot.GetComponentsInChildren<BoxCollider>().Where(c => c.name == "Plate Colliders")) c.enabled = false;
    }

    // Rendered snapshots of the coral's path from the ground intake to the claw, and of a held algae, from two sides.
    // Needs a run without -nographics. Images go to $SNAP_DIR (default unity-tests-971/snaps).
    [UnityTest]
    [Timeout(600000)]
    public IEnumerator Visual()
    {
        yield return Boot();
        var dir = Environment.GetEnvironmentVariable("SNAP_DIR") ?? "/home/seb/Desktop/MoSimulator/unity-tests-971/snaps";
        System.IO.Directory.CreateDirectory(dir);
        var t = _robot.transform;
        var cam = new GameObject("SnapCam").AddComponent<Camera>();
        cam.fieldOfView = 40;
        cam.nearClipPlane = 0.02f;
        var rt = new RenderTexture(960, 640, 24);
        cam.targetTexture = rt;
        var tex = new Texture2D(960, 640, TextureFormat.RGB24, false);
        // Rendering is slow: fix game time per frame so snapshots are evenly spaced in game time.
        Time.captureDeltaTime = 0.02f;
        // Show the claw's colliders as translucent red boxes (visual only, no collider) that move with them.
        var shown = new Material(Shader.Find("Sprites/Default")) { color = new Color(1, 0, 0, 0.35f) };
        foreach (var bc in new[] { "wrist", "intake", "climber" }.SelectMany(f => ((Component)Field(f)).GetComponentsInChildren<BoxCollider>(true)).Where(c => !c.isTrigger))
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(cube.GetComponent<Collider>());
            cube.GetComponent<Renderer>().sharedMaterial = shown;
            cube.transform.SetParent(bc.transform, false);
            cube.transform.localPosition = bc.center;
            cube.transform.localScale = bc.size;
            L($"collider shown {bc.name} size={Vector3.Scale(bc.size, bc.transform.lossyScale):F3}");
        }
        // cut > 0: a cutaway, clipping everything nearer than (dist - cut) so the focus is seen in section.
        void Snap(string name, Vector3 focus, Vector3 fromLocalDir, float dist, float cut = 0)
        {
            cam.transform.position = focus + t.TransformDirection(fromLocalDir.normalized) * dist;
            cam.transform.LookAt(focus);
            cam.nearClipPlane = cut > 0 ? Mathf.Max(0.02f, dist - cut) : 0.02f;
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 960, 640), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
        }

        // ---- coral: ground intake to the claw
        yield return DropHeld();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1f);
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        yield return new WaitForSeconds(1f);
        var gi = Field("groundCoralIntake") as ReefscapeGamePieceIntake;
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(3f);
        var box = Boxes(gi)[0].transform;
        // The deployed ground intake and its colliders, from the side, behind and above.
        var intakeC = ((Component)Field("intake")).GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; }).center;
        Snap("intake_side", intakeC, new Vector3(-1, 0.15f, 0), 1.3f);
        Snap("intake_back", intakeC, new Vector3(0, 0.3f, -1), 1.3f);
        Snap("intake_top", intakeC, new Vector3(0, 1, -0.05f), 1.3f);
        Snap("intake_quarter", intakeC, new Vector3(-0.7f, 0.5f, -0.7f), 1.4f);
        var coral = FreePiece("Coral", t.position);
        var bp = box.position;
        // The coral lies 25 cm outside the intake and the robot drives onto it, like in a match.
        var outward = Vector3.ProjectOnPlane(bp - t.position, Vector3.up).normalized;
        Put(coral, new Vector3(bp.x, 0.07f, bp.z) + outward * 0.25f, Along(coral, t.right));
        var robotRb = _robot.GetComponent<Rigidbody>();
        var eeT = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        var giT = (Field("groundIntakeState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        L($"snap targets claw(robot-local)={(eeT ? t.InverseTransformPoint(eeT.position).ToString("F3") : "-")} " +
          $"groundState(robot-local)={(giT ? t.InverseTransformPoint(giT.position).ToString("F3") : "-")} groundIntakeTarget={((gi ? typeof(RobotFramework.Components.GamePieceIntake<Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>).GetField("target", F)?.GetValue(gi) : null) as Transform)?.name ?? "-"}");
        for (var i = 0; i < 60; i++)
        {
            if (!NodeOf("Coral").HasPiece()) robotRb.AddForce(outward * 400f);
            var focus = eeT ? (eeT.position + coral.transform.position) / 2 : coral.transform.position;
            // Cutaways from the robot's side (the coral's travel plane) and from above.
            Snap($"coral_{i:D2}_side", focus, new Vector3(-1, 0.05f, 0), 1.2f, 0.3f);
            Snap($"coral_{i:D2}_top", focus, new Vector3(0, 1, 0.05f), 1.2f, 0.3f);
            var toClaw = eeT ? $" distToClaw={(coral.transform.position - eeT.position).magnitude:F3} angleToClaw={Quaternion.Angle(coral.transform.rotation, eeT.rotation):F0}" : "";
            L($"snap coral {i} t={Time.time:F2} pos(robot-local)={t.InverseTransformPoint(coral.transform.position):F3}{toClaw} st={NodeOf("Coral").currentStateNum} at={NodeOf("Coral").atTarget}");
            yield return null;
        }
        Keys();
        yield return new WaitForSeconds(1.5f);
        var ee = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        if (ee)
        {
            Snap("coral_held_side", ee.position, new Vector3(-1, 0.05f, 0), 1.0f, 0.25f);
            Snap("coral_held_front", ee.position, new Vector3(0, 0.05f, 1), 1.0f, 0.25f);
            Snap("coral_held_top", ee.position, new Vector3(0, 1, 0.05f), 1.0f, 0.25f);
        }

        // ---- algae held from the ground
        yield return DropHeld();
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
        var ai = Field("algaeIntake") as Component;
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(3f);
        var algae = FreePiece("Algae", t.position);
        if (ai) Put(algae, ai.transform.position + t.forward * 0.1f, Quaternion.identity);
        yield return new WaitForSeconds(3f);
        Keys();
        yield return new WaitForSeconds(2f);
        if (NodeOf("Algae").HasPiece())
        {
            var held = NodeOf("Algae").controller;
            L($"algaeHold snap {AlgaeGap(held)}");
            var ballCol = held.GetComponentInChildren<SphereCollider>(true);
            var c = ballCol ? ballCol.transform.TransformPoint(ballCol.center) : held.transform.position;
            Snap("algae_held_side", c, new Vector3(-1, 0.05f, 0), 1.2f, 0.35f);
            Snap("algae_held_front", c, new Vector3(0, 0.05f, 1), 1.2f, 0.35f);
            Snap("algae_held_back", c, new Vector3(0, 0.05f, -1), 1.2f, 0.35f);
            Snap("algae_held_top", c, new Vector3(0, 1, 0.05f), 1.2f, 0.35f);
            Snap("algae_held_wide", c, new Vector3(-1, 0.3f, 0.6f), 2.0f);
        }
        // ---- bumpers and their numbers, from each side
        foreach (var (view, from) in new[] { ("front", new Vector3(0, 0.12f, 1)), ("back", new Vector3(0, 0.12f, -1)), ("left", new Vector3(-1, 0.12f, 0)), ("right", new Vector3(1, 0.12f, 0)) })
            Snap($"bumper_{view}", t.position + Vector3.up * 0.1f, from, 1.4f);

        // ---- climber deployed, with its colliders
        yield return DropHeld();
        yield return Tap(Key.C);
        yield return new WaitForSeconds(2.5f);
        var climberC = ((Component)Field("climber")).GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; }).center;
        L($"climber snap centre(robot-local)={t.InverseTransformPoint(climberC):F3}");
        Snap("climber_side", climberC, new Vector3(-1, 0.1f, 0), 1.0f);
        Snap("climber_back", climberC, new Vector3(0, 0.2f, -1), 1.0f);
        Snap("climber_top", climberC, new Vector3(0, 1, -0.05f), 1.0f);
        Snap("climber_quarter", climberC, new Vector3(-0.7f, 0.4f, -0.7f), 1.1f);
        Snap("climber_quarter2", climberC, new Vector3(0.7f, 0.4f, -0.7f), 1.1f);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1.5f);

        // ---- L4: score facing the reef, then look at how the coral sits on the branch
        Time.captureDeltaTime = 0;
        yield return DropHeld();
        yield return LoadCoral();
        var reef = GameObject.Find(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? "BlueReef" : "RedReef").transform.position;
        var ang = Mathf.Deg2Rad * 90f;
        var d = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
        PlaceRobot(reef + d * 1.75f, -d);
        yield return new WaitForSeconds(0.5f);
        Keys(Key.Q);
        yield return new WaitForSeconds(2f);
        yield return Tap(Key.P);
        yield return new WaitForSeconds(3f);
        var scored = NodeOf("Coral").controller;
        Keys(_held.Append(Key.K).ToArray());
        yield return new WaitForSeconds(0.2f);
        Keys(_held.Where(x => x != Key.K).ToArray());
        var crb = scored ? scored.GetComponent<Rigidbody>() : null;
        for (var k = 0; k < 6 && crb; k++)
        {
            L($"l4seat t+{0.2f + k * 0.4f:F1}s coralY={crb.position.y:F3} vel={crb.velocity.magnitude:F2} tilt={Vector3.Angle(crb.transform.forward, Vector3.up):F0}");
            yield return new WaitForSeconds(0.4f);
        }
        if (crb)
        {
            var branch = Physics.OverlapSphere(crb.position, 0.4f).Where(c => c.name == "L4" && c.isTrigger).OrderBy(c => (c.bounds.center - crb.position).sqrMagnitude).FirstOrDefault();
            if (branch) L($"l4seat coral-centre vs L4 trigger centre: {(crb.position - branch.bounds.center):F3} triggerSize={branch.bounds.size:F3}");
            Time.captureDeltaTime = 0.02f;
            Snap("l4_seated_side", crb.position, t.InverseTransformDirection(Vector3.Cross(Vector3.up, d)), 1.0f);
            Snap("l4_seated_front", crb.position, t.InverseTransformDirection(d + Vector3.up * 0.3f), 1.2f);
            Snap("l4_seated_back", crb.position, t.InverseTransformDirection(-d + Vector3.up * 0.6f), 1.2f);
            Time.captureDeltaTime = 0;
        }
        L("DONE");
    }

    [UnityTest]
    [Timeout(600000)]
    public IEnumerator Intakes()
    {
        yield return Boot();
        var fwd = _robot.transform.forward;

        // ---- ground coral
        yield return DropHeld();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1f);
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        yield return new WaitForSeconds(1f);
        var ee = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        var t = _robot.transform;
        var gi = Field("groundCoralIntake") as ReefscapeGamePieceIntake;
        L("geometry ground " + Geometry());
        var placements = new[] { (0f, true), (0.08f, true), (-0.08f, false), (0.15f, true), (-0.15f, true), (0.04f, true) }; // lateral offset m, coral lying sideways
        // The last placement is driven onto right after intake is pressed, while the superstructure is still moving.
        // LIFTVARIANTS="base;hinge=50;pivot=300;ramp=off;kick=off;pull=5": one intake change per pickup, on a centred sideways
        // coral, to find what lifts the robot. Each change is undone after its pickup.
        var variants = (System.Environment.GetEnvironmentVariable("LIFTVARIANTS") ?? "").Split(';', System.StringSplitOptions.RemoveEmptyEntries);
        if (variants.Length > 0) placements = variants.Select(_ => (0f, true)).ToArray();
        var undo = new System.Collections.Generic.List<System.Action>();
        for (var rep = 0; rep < placements.Length; rep++)
        {
            foreach (var u in undo) u();
            undo.Clear();
            if (variants.Length > 0)
            {
                var intakeRoot = (Field("intake") as Component).transform;
                var kv = variants[rep].Split('=');
                switch (kv[0])
                {
                    case "hinge":
                        foreach (var hj in intakeRoot.GetComponentsInChildren<HingeJoint>())
                        {
                            var m = hj.motor; var old = m.force; m.force = float.Parse(kv[1]); hj.motor = m;
                            undo.Add(() => { var r = hj.motor; r.force = old; hj.motor = r; });
                        }
                        break;
                    case "pivot":
                        var pj = intakeRoot.GetComponent<ConfigurableJoint>();
                        var d = pj.angularXDrive; var oldF = d.maximumForce; d.maximumForce = float.Parse(kv[1]); pj.angularXDrive = d;
                        undo.Add(() => { var r = pj.angularXDrive; r.maximumForce = oldF; pj.angularXDrive = r; });
                        break;
                    case "ramp":
                    case "kick":
                        foreach (var c in intakeRoot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.name.StartsWith(kv[0] == "ramp" ? "Ramp" : "Kicker")))
                        {
                            c.enabled = false;
                            undo.Add(() => c.enabled = true);
                        }
                        break;
                    case "pull":
                        var giPull = Field("groundCoralIntake") as ReefscapeGamePieceIntake;
                        var fi = typeof(ReefscapeGamePieceIntake).BaseType.GetField("intakeForce", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                        var oldPull = (float)fi.GetValue(giPull); fi.SetValue(giPull, float.Parse(kv[1]));
                        undo.Add(() => fi.SetValue(giPull, oldPull));
                        break;
                }
                L($"RESULT ground rep={rep} variant {variants[rep]} pivotMaxForce={intakeRoot.GetComponent<ConfigurableJoint>().angularXDrive.maximumForce}");
            }
            yield return DropHeld();
            Keys(Key.LeftShift);
            var early = variants.Length == 0 && rep == placements.Length - 1;
            // Superstructure to the ground setpoint; when the ground rollers start spinning, after the press.
            var hinges = (Field("intake") as Component).GetComponentsInChildren<HingeJoint>().Where(h => h.useMotor).ToArray();
            var rollersAfter = -1f;
            for (var e = 0f; e < (early ? 0.2f : 3f); e += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                if (rollersAfter < 0 && hinges.Any(h => Mathf.Abs(h.velocity) > 100f)) rollersAfter = e;
            }
            if (!early) L($"RESULT ground rep={rep} rollersStartAfter={rollersAfter:F2}s");
            if (rep == 0)
            {
                // Each physical intake roller: does its underside (where it meets a coral on the carpet) move toward the
                // robot (pull in) or away (throw out)?
                foreach (var roller in (Field("intake") as Component).GetComponentsInChildren<GenericRoller>())
                {
                    var rrb = roller.GetComponent<Rigidbody>();
                    var cap = roller.GetComponentInChildren<CapsuleCollider>();
                    var c = cap ? cap.bounds.center : roller.transform.position;
                    var r = cap ? cap.radius * cap.transform.lossyScale.x : 0.025f;
                    var bottom = c + Vector3.down * r;
                    var surface = rrb ? Vector3.Cross(rrb.angularVelocity, bottom - c) + rrb.GetPointVelocity(c) - _robot.GetComponent<Rigidbody>().velocity : Vector3.zero;
                    var toRobot = Vector3.ProjectOnPlane(t.position - c, Vector3.up).normalized;
                    L($"roller {roller.name} at(robot-local)={t.InverseTransformPoint(c):F3} r={r:F3} angVel={(rrb ? rrb.angularVelocity.magnitude : 0):F0}rad/s " +
                      $"undersideSpeed={surface.magnitude:F2}m/s towardRobot={Vector3.Dot(surface, toRobot):F2}m/s");
                }
            }
            // The main roller's compliance joint: which way its free swing (-50..0 deg about the joint X axis) moves the roller.
            var comply = (Field("intake") as Component).GetComponentsInChildren<ConfigurableJoint>().FirstOrDefault(j => j.name == "ComplianceThing");
            var complyRest = comply ? comply.transform.localRotation : Quaternion.identity;
            float ComplyAngle()
            {
                var q = Quaternion.Inverse(complyRest) * comply.transform.localRotation;
                q.ToAngleAxis(out var a, out var ax);
                return a * Mathf.Sign(Vector3.Dot(ax, comply.axis));
            }
            if (comply && rep == 0)
            {
                var axisW = comply.transform.TransformDirection(comply.axis);
                var roller = comply.GetComponentInChildren<GenericRoller>().transform.position;
                var swing = Vector3.Cross(-axisW, roller - comply.transform.position);
                L($"comply negativeSwingMovesRoller(robot-local)={t.InverseTransformDirection(swing).normalized:F2} angle={ComplyAngle():F1}");
            }
            var box = Boxes(gi)[0].transform;
            var coral = FreePiece("Coral", t.position);
            Assert.NotNull(coral, "no free coral");
            var (off, sideways) = placements[rep];
            var bp = box.position;
            // Like a match: the coral lies on the carpet 25 cm outside the intake and the robot drives onto it
            // (placing it inside the intake can overlap the rollers and make physics throw it out).
            var outward = Vector3.ProjectOnPlane(bp - t.position, Vector3.up).normalized;
            Put(coral, new Vector3(bp.x, 0.07f, bp.z) + t.right * off + outward * 0.25f, Along(coral, sideways ? t.right : t.forward));
            var robotRb = _robot.GetComponent<Rigidbody>();

            // Speed trace from pickup to the claw, every physics step, until the coral is held at the claw.
            var trace = new System.Collections.Generic.List<(float t, float v, float d, int st, bool at)>();
            var t1 = Time.time + 6f;
            var lastPos = coral.transform.position;
            var stallLogged = false;
            var y0 = t.position.y;
            var maxLift = 0f;
            var loggedLift = 0f;
            var maxAboveClaw = float.NegativeInfinity;
            float minComply = 0, maxComply = 0;
            while (Time.time < t1)
            {
                if (NodeOf("Coral").currentStateNum == 0 && !NodeOf("Coral").HasPiece()) robotRb.AddForce(outward * 400f);
                yield return new WaitForFixedUpdate();
                if (t.position.y - y0 > loggedLift + 0.003f)
                {
                    loggedLift = t.position.y - y0;
                    var cc = coral.GetComponentInChildren<Collider>();
                    var touching = Physics.OverlapBox(cc.bounds.center, cc.bounds.extents + Vector3.one * 0.005f)
                        .Where(c => c.transform.IsChildOf(t)).Select(c => c.name).Distinct();
                    L($"lift rep={rep} t={Time.time:F2} lift={(t.position.y - y0) * 100:F1}cm comply={(comply ? ComplyAngle() : 0):F1}deg coralY={coral.transform.position.y:F3} touching=[{string.Join(", ", touching)}]");
                }
                maxLift = Mathf.Max(maxLift, t.position.y - y0);
                if (ee) maxAboveClaw = Mathf.Max(maxAboveClaw, coral.transform.position.y - ee.position.y);
                if (comply) { minComply = Mathf.Min(minComply, ComplyAngle()); maxComply = Mathf.Max(maxComply, ComplyAngle()); }
                var pos = coral.transform.position;
                var v = (pos - lastPos).magnitude / Time.fixedDeltaTime;
                lastPos = pos;
                var node = NodeOf("Coral");
                var d = ee ? (pos - ee.position).magnitude : -1f;
                trace.Add((Time.time, v, d, node.currentStateNum, node.atTarget));
                if (!stallLogged && trace.Count > 40 && node.currentStateNum == 0 && trace.Skip(trace.Count - 20).All(x => x.v < 0.05f))
                {
                    stallLogged = true;
                    var touching = Physics.OverlapBox(coral.GetComponentInChildren<Collider>().bounds.center,
                            coral.GetComponentInChildren<Collider>().bounds.extents + Vector3.one * 0.01f)
                        .Where(c => c.transform.IsChildOf(t)).Select(c => c.name).Distinct();
                    L($"groundStall rep={rep} coral(robot-local)={t.InverseTransformPoint(pos):F3} distToClaw={d:F3} touching=[{string.Join(", ", touching)}]");
                }
                if (node.currentStateNum > 0 && node.atTarget && d < 0.03f && trace.Count > 5) break; // fully in the claw
            }
            var moving = trace.FindIndex(x => x.v > 0.2f);
            var done = trace.FindIndex(x => x.st > 0 && x.at && x.d < 0.03f);
            for (var k = 0; k < trace.Count && k < 120; k += 4)
                L($"groundTrace rep={rep} t={trace[k].t:F3} speed={trace[k].v:F2}m/s distToClaw={trace[k].d:F3}m st={trace[k].st} at={trace[k].at}");
            if (moving >= 0 && done > moving)
            {
                var seg = trace.GetRange(moving, done - moving);
                var stall = 0;
                var longest = 0;
                foreach (var x in seg) { stall = x.v < 0.05f && x.d > 0.03f ? stall + 1 : 0; longest = Mathf.Max(longest, stall); }
                L($"RESULT ground rep={rep} held=True pickupToClaw={(trace[done].t - trace[moving].t):F2}s " +
                  $"minSpeed={seg.Min(x => x.v):F2}m/s longestStall={longest * Time.fixedDeltaTime:F3}s finalDistToClaw={trace[done].d:F3}m");
            }
            else L($"RESULT ground rep={rep} held=False (moving={moving} done={done})");
            L($"RESULT ground rep={rep} robotLift={maxLift * 100:F1}cm complyRange={minComply:F1}..{maxComply:F1}deg maxCoralAboveClaw={maxAboveClaw * 100:F1}cm");
            Keys();
            yield return Watch(2f, "groundRelease");
            L($"RESULT ground rep={rep} still held after stow={NodeOf("Coral").HasPiece()}");
        }

        // ---- the deployed ground intake's side plates stop a loose coral thrown at them from the side
        yield return DropHeld();
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        foreach (var plate in ((Component)Field("intake")).GetComponentsInChildren<MeshFilter>().Where(m => m.name.Contains("Intake Plate")))
        {
            var cols = plate.GetComponents<BoxCollider>();
            var centre = plate.GetComponent<Renderer>().bounds.center;
            var outward = Mathf.Sign(t.InverseTransformPoint(centre).x) * t.right;
            var probe = FreePiece("Coral", centre);
            Put(probe, centre + outward * 0.25f, Along(probe, t.forward));
            var prb = probe.GetComponent<Rigidbody>();
            prb.velocity = -outward * 2f;
            var crossed = false;
            for (var k = 0; k < 15; k++) // 0.3 s: it reaches the plate after ~0.1 s
            {
                yield return new WaitForFixedUpdate();
                if (Vector3.Dot(probe.transform.position - centre, outward) < -0.05f) crossed = true;
            }
            L($"RESULT sidePlate {plate.name} colliders={cols.Length} layer={LayerMask.LayerToName(plate.gameObject.layer)} " +
              $"stopped={!crossed} coralSide={Vector3.Dot(probe.transform.position - centre, outward):F2}m");
        }
        Keys();
        yield return new WaitForSeconds(1.5f);

        // ---- HP coral
        yield return DropHeld();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        var stations = Object.FindObjectsByType<Games.Reefscape.FieldScripts.CoralStation>(FindObjectsSortMode.None)
            .Where(s => s.Alliance == _robot.Alliance).ToArray();
        L($"stations {string.Join(",", stations.Select(s => s.name + s.transform.position))}");
        var spawn = (Transform)typeof(Games.Reefscape.FieldScripts.CoralStation).GetField("coralSpawnpoint", F)
            .GetValue(stations[0]);
        var hpi = Field("hpCoralIntake") as ReefscapeGamePieceIntake;
        foreach (var facing in new[] { 1f, -1f })
        {
            var sf = spawn.forward;
            sf.y = 0;
            sf.Normalize();
            PlaceRobot(spawn.position + sf * 0.75f, -sf * facing);
            yield return new WaitForSeconds(1f);
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(1.5f);
            var box = Boxes(hpi)[0].transform;
            L($"hp spawn(robot-local)={t.InverseTransformPoint(spawn.position):F2} box={t.InverseTransformPoint(box.position):F2} " + Geometry());
            var end = Time.time + 6f;
            while (Time.time < end && !NodeOf("Coral").HasPiece())
            {
                var c = FreePiece("Coral", box.position);
                var rb = c ? c.GetComponent<Rigidbody>() : null;
                // Robot parts dug into the station (anything static that isn't a game piece), deepest first.
                var dug = _robot.GetComponentsInChildren<Collider>().Where(r => !r.isTrigger && r.enabled)
                    .SelectMany(r => Physics.OverlapBox(r.bounds.center, r.bounds.extents + Vector3.one * 0.005f)
                        .Where(o => !o.isTrigger && !o.transform.IsChildOf(t) && o.attachedRigidbody == null &&
                                    Physics.ComputePenetration(r, r.transform.position, r.transform.rotation, o, o.transform.position, o.transform.rotation, out _, out var dd) && dd > 0.003f)
                        .Select(o => { Physics.ComputePenetration(r, r.transform.position, r.transform.rotation, o, o.transform.position, o.transform.rotation, out _, out var dd2); return (name: $"{r.transform.parent.name}/{r.name}>{o.transform.parent?.name}/{o.name}", d: dd2); }))
                    .OrderByDescending(x => x.d).Take(5).Select(x => $"{x.name}:{x.d * 100:F1}cm");
                L($"{(facing > 0 ? "hpFront" : "hpBack")} {Status()} nearestCoral(box-local)={(c ? box.InverseTransformPoint(c.transform.position).ToString("F2") : "-")} " +
                  $"vel={(rb ? rb.velocity.ToString("F1") : "-")} dugIn=[{string.Join(" ", dug)}] toStation={Vector3.Dot(spawn.position - t.position, sf):F2}m");
                // Like a driver: keep pushing into the station while waiting for the coral.
                for (var k = 0f; k < 0.25f; k += Time.fixedDeltaTime)
                {
                    _robot.GetComponent<Rigidbody>().AddForce(-sf * 300f);
                    yield return new WaitForFixedUpdate();
                }
            }
            Keys();
            yield return new WaitForSeconds(1f);
            L($"RESULT hp coral facing={facing} held={NodeOf("Coral").HasPiece()}");
            yield return DropHeld();
        }

        // ---- algae: ground, lollipop (L1 in algae mode), low and high reef (L2 / L3 without coral)
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
        var ai = Field("algaeIntake") as Component;
        foreach (var (label, key) in new[] { ("groundAlgae", Key.None), ("lollipop", Key.U), ("lowAlgae", Key.I), ("highAlgae", Key.O) })
        {
            yield return DropHeld();
            if (key != Key.None) yield return Tap(key);
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(3f);
            var algae = FreePiece("Algae", t.position);
            Assert.NotNull(algae, "no free algae");
            if (ai) Put(algae, ai.transform.position + t.forward * 0.1f, Quaternion.identity);
            yield return Watch(4f, label, () => ai ? $"algaeDist={(algae.transform.position - ai.transform.position).magnitude:F2}" : "");
            Keys();
            yield return Watch(2f, label + "Release");
            L($"RESULT {label} held={NodeOf("Algae").HasPiece()}");
            if (!NodeOf("Algae").HasPiece()) continue;
            var score = label is "groundAlgae" or "lowAlgae" ? Key.U : Key.P; // L1 -> Processor, L4 -> Barge
            var held = NodeOf("Algae").controller;
            L($"algaeHold {label} {AlgaeGap(held)}");
            yield return Tap(score);
            yield return Watch(2.5f, label + (score == Key.U ? "Proc" : "Barge"));
            yield return Tap(Key.K);
            yield return new WaitForSeconds(0.3f);
            var rb = held.GetComponent<Rigidbody>();
            var v = rb ? _robot.transform.InverseTransformDirection(rb.velocity) : Vector3.zero;
            L($"RESULT {label} {(score == Key.U ? "processor" : "barge")} released={!NodeOf("Algae").HasPiece()} " +
              $"vel(robot-local)={v:F2} pos={_robot.transform.InverseTransformPoint(held.transform.position):F2}");
            yield return new WaitForSeconds(1f);
        }
        L("DONE");
    }

    // ---------------------------------------------------------------- scoring (outtake on each level, processor, barge)
    static readonly FieldInfo BlueScore = typeof(GameSystems.Scoring.ScoreHandler<Games.Reefscape.Scoring.ReefscapeScoreData,
        Games.Reefscape.Scoring.ReefscapeScoreUI>).GetField("BlueScoreData", F);
    static readonly FieldInfo RedScore = typeof(GameSystems.Scoring.ScoreHandler<Games.Reefscape.Scoring.ReefscapeScoreData,
        Games.Reefscape.Scoring.ReefscapeScoreUI>).GetField("RedScoreData", F);

    (int coral, int pts, int net, int algae) Score()
    {
        var h = Object.FindFirstObjectByType<Games.Reefscape.Scoring.ReefscapeScoreHandler>();
        var d = (Games.Reefscape.Scoring.ReefscapeScoreData)(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? BlueScore : RedScore).GetValue(h);
        return (d.CoralScored, d.CoralPoints, d.NetPoints, d.AlgaeScored);
    }

    IEnumerator LoadCoral()
    {
        if (NodeOf("Coral").HasPiece()) yield break;
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Coral) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        var hpi = Field("hpCoralIntake") as ReefscapeGamePieceIntake;
        var box = Boxes(hpi)[0].transform;
        var loading = SpareCoral(box.position);
        Put(loading, box.position, box.rotation);
        yield return new WaitForSeconds(1.5f);
        var during = Status();
        Keys();
        yield return new WaitForSeconds(2f);
        L($"loaded coral={NodeOf("Coral").HasPiece()}" + (NodeOf("Coral").HasPiece() ? "" : $" piece={(loading ? loading.name : "none")} at {during}"));
    }

    IEnumerator LoadAlgae()
    {
        if (NodeOf("Algae").HasPiece()) yield break;
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        var ai = Field("algaeIntake") as ReefscapeGamePieceIntake;
        var box = Boxes(ai)[0].transform;
        Put(FreePiece("Algae", box.position), box.position, Quaternion.identity);
        yield return new WaitForSeconds(1.5f);
        Keys();
        yield return new WaitForSeconds(2f);
        L($"loaded algae={NodeOf("Algae").HasPiece()}");
    }

    // The piece's own colliders (while held they are re-parented under the robot).
    static System.Collections.Generic.HashSet<Collider> PieceColliders(PieceCtrl piece)
    {
        var f = typeof(PieceCtrl).GetField("_colliders", F);
        var arr = f?.GetValue(piece) as Collider[];
        var set = new System.Collections.Generic.HashSet<Collider>(arr ?? Array.Empty<Collider>());
        foreach (var c in piece.GetComponentsInChildren<Collider>(true)) set.Add(c);
        return set;
    }

    // Robot colliders (non-trigger) that overlap a held piece's colliders, with penetration depth.
    string Overlaps(PieceCtrl piece)
    {
        if (!piece) return "-";
        var hits = new System.Collections.Generic.List<string>();
        var own = PieceColliders(piece);
        // Anything solid near the piece (robot parts, reef, field), not just the robot.
        var robotCols = Physics.OverlapSphere(piece.transform.position, 0.35f)
            .Where(c => c.enabled && !c.isTrigger && !own.Contains(c)).ToArray();
        foreach (var pc in own.Where(c => c && c.enabled && !c.isTrigger))
        foreach (var rc in robotCols)
        {
            if (Physics.ComputePenetration(pc, pc.transform.position, pc.transform.rotation, rc, rc.transform.position,
                    rc.transform.rotation, out _, out var depth))
                hits.Add($"{(rc.transform.IsChildOf(_robot.transform) ? "robot" : rc.transform.root.name)}:{rc.transform.parent?.name}/{rc.name}({rc.GetType().Name},{depth * 1000:F0}mm)");
        }
        return hits.Count == 0 ? "none" : string.Join(", ", hits.Distinct());
    }

    // Claw and held coral vs the nearest scoring target of this level, in robot-local metres.
    // Gap between a held algae ball and the robot parts around it (negative = sunk in).
    string AlgaeGap(Component held)
    {
        var t = _robot.transform;
        var ball = held.GetComponentInChildren<SphereCollider>(true);
        var centre = ball ? ball.transform.TransformPoint(ball.center) : held.transform.position;
        var radius = ball ? ball.radius * ball.transform.lossyScale.x : 0.2f;
        var gaps = _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled && !(c is MeshCollider m && !m.convex))
            .Where(c => !c.transform.IsChildOf(held.transform) && c != ball)
            .Select(c => (c, d: Vector3.Distance(c.ClosestPoint(centre), centre) - radius)).OrderBy(x => x.d).Take(4)
            .Select(x => $"{x.c.transform.parent?.name}/{x.c.name}:{x.d * 100:F1}cm");
        return $"radius={radius:F3} ballLocal={t.InverseTransformPoint(centre):F3} nearest=[{string.Join(", ", gaps)}]";
    }

    string ScoreGeometry(string level)
    {
        var t = _robot.transform;
        string P(Vector3 w) { var l = t.InverseTransformPoint(w); return $"({l.x:F2},{l.y:F2},{l.z:F2})"; }
        string D(Vector3 w) { var l = t.InverseTransformDirection(w); return $"({l.x:F2},{l.y:F2},{l.z:F2})"; }
        var claw = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        var coral = NodeOf("Coral").controller;
        var targets = level == "L1"
            ? Object.FindObjectsByType<Games.Reefscape.Scoring.TroughScorer>(FindObjectsSortMode.None)
                .SelectMany(x => (BoxCollider[])typeof(Games.Reefscape.Scoring.TroughScorer).GetField("troughTriggers", F).GetValue(x))
            : Object.FindObjectsByType<Games.Reefscape.Scoring.Scorers.ReefPoleScorer>(FindObjectsSortMode.None)
                .SelectMany(x => x.GetComponentsInChildren<BoxCollider>(true)).Where(b => b.name == level);
        var best = targets.Where(b => b).OrderBy(b => (b.bounds.center - claw.position).sqrMagnitude).FirstOrDefault();
        return $"claw={P(claw.position)} coral={(coral ? P(coral.transform.position) + " coralFwd=" + D(coral.transform.forward) + " coralUp=" + D(coral.transform.up) : "-")} " +
               $"target={(best ? best.transform.parent.name + "/" + best.name + P(best.bounds.center) + " size=" + best.bounds.size.ToString("F2") : "-")}";
    }

    // World path of a released piece for ~1.5 s, against a scoring box (centre/size, world).
    IEnumerator TraceRelease(string label, PieceCtrl piece, Vector3 boxCenter, Vector3 boxSize)
    {
        var rb = piece ? piece.GetComponent<Rigidbody>() : null;
        L($"{label} box centre={boxCenter:F2} size={boxSize:F2}");
        for (var k = 0; k < 75 && rb; k++)
        {
            yield return new WaitForFixedUpdate();
            if (k % 5 == 0) L($"{label} k={k} pos={piece.transform.position:F2} vel={rb.velocity:F2} touching=[{Overlaps(piece)}]");
        }
    }

    static (Vector3, Vector3) BargeBox(Component scorer)
    {
        var b = scorer.GetComponent<BoxCollider>();
        return b ? (b.bounds.center, b.bounds.size) : (scorer.transform.position, Vector3.zero);
    }

    static (Vector3, Vector3) Box(Component scorer, string field)
    {
        var b = scorer.GetType().GetField(field, F)?.GetValue(scorer) as BoxCollider;
        return b ? (b.bounds.center, b.bounds.size) : (scorer.transform.position, Vector3.zero);
    }

    // ---- ground algae like a match: the ball lies on the carpet and the robot drives onto it in the ground algae pose
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator GroundAlgae()
    {
        yield return Boot();
        var t = _robot.transform;
        var rb = _robot.GetComponent<Rigidbody>();
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
        var ai = Field("algaeIntake") as ReefscapeGamePieceIntake;
        // GA_POSES="arm,wrist,elevator;...": try ground algae poses (in memory only), centred ball for each.
        var poseList = (Environment.GetEnvironmentVariable("GA_POSES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var gaSetpoint = Field("groundAlgae");
        var trials = poseList.Length == 0
            ? new[] { ("", 0f), ("", 0.12f), ("", -0.12f) }
            : poseList.Select(p => (p, 0f)).ToArray();
        foreach (var (poseSpec, lateral) in trials)
        {
            const float side = 1f;
            if (poseSpec != "")
            {
                var v = poseSpec.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                gaSetpoint.GetType().GetField("armAngle").SetValue(gaSetpoint, v[0]);
                gaSetpoint.GetType().GetField("wristAngle").SetValue(gaSetpoint, v[1]);
                gaSetpoint.GetType().GetField("elevatorHeight").SetValue(gaSetpoint, v[2]);
                L($"groundAlgae trying pose arm={v[0]} wrist={v[1]} elev={v[2]}");
            }
            yield return DropHeld();
            PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
            yield return new WaitForSeconds(0.5f);
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(2f);
            var box = Boxes(ai)[0];
            var boxLocal = t.InverseTransformPoint(box.bounds.center);
            // Which way the claw faces (its side of the robot), and the ball 0.5 m further out on the carpet.
            var outDir = boxLocal.z >= 0 ? t.forward : -t.forward;
            var algae = FreePiece("Algae", t.position);
            var r = algae.GetComponentInChildren<SphereCollider>();
            var radius = r ? r.radius * r.transform.lossyScale.x : 0.2f;
            var spot = new Vector3(box.bounds.center.x, radius + 0.005f, box.bounds.center.z) + Vector3.ProjectOnPlane(outDir, Vector3.up).normalized * 0.3f + t.right * lateral;
            Put(algae, spot, Quaternion.identity);
            L($"groundAlgae side={side} lateral={lateral} pose={Status()} box(robot-local)={boxLocal:F2} boxSize={box.bounds.size:F2} boxBottomAboveFloor={box.bounds.min.y:F2} ballCentre={spot.y:F2} r={radius:F2}");
            var start = t.position;
            var held = false;
            for (var e = 0f; e < 4f && !held; e += Time.fixedDeltaTime)
            {
                if (!NodeOf("Algae").HasPiece()) rb.AddForce(Vector3.ProjectOnPlane(outDir, Vector3.up).normalized * 400f);
                yield return new WaitForFixedUpdate();
                held = NodeOf("Algae").HasPiece();
            }
            var rel = t.InverseTransformPoint(algae.transform.position);
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                var dir = Environment.GetEnvironmentVariable("SNAP_DIR") ?? "/home/seb/Desktop/MoSimulator/unity-tests-971/snaps_ga";
                System.IO.Directory.CreateDirectory(dir);
                var cam = new GameObject("GaCam").AddComponent<Camera>();
                cam.fieldOfView = 40;
                var rt = new RenderTexture(960, 640, 24); cam.targetTexture = rt;
                var tex = new Texture2D(960, 640, TextureFormat.RGB24, false);
                var focus = (box.bounds.center + algae.transform.position) / 2;
                foreach (var (view, from) in new[] { ("side", focus + t.right * 1.4f + Vector3.up * 0.2f), ("top", focus + Vector3.up * 1.6f + t.forward * 0.01f), ("front", focus + outDir * 1.5f + Vector3.up * 0.4f) })
                {
                    cam.transform.position = from; cam.transform.LookAt(focus); cam.Render();
                    RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 960, 640), 0, 0); tex.Apply(); RenderTexture.active = null;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, $"ga_{trials.ToList().IndexOf((poseSpec, lateral))}_{view}.png"), tex.EncodeToPNG());
                }
                Object.Destroy(cam.gameObject);
            }
            L($"RESULT groundAlgaeFloor side={side} lateral={lateral} held={held} robotMoved={(t.position - start).magnitude:F2}m ball(robot-local)={rel:F2} ballToBox={(algae.transform.position - box.bounds.center).magnitude:F2}m");
            Keys();
            yield return new WaitForSeconds(1f);
            if (!held) algae.gameObject.SetActive(false); // a missed ball mustn't be picked up by the next attempt
        }
    }

    // ---- tutorial card pictures (run with graphics): scoring facing the reef and facing away, the ground and station
    // intakes, and a hero shot
    [UnityTest]
    [Timeout(400000)]
    public IEnumerator CardShots()
    {
        yield return Boot();
        var t = _robot.transform;
        var dir = Environment.GetEnvironmentVariable("SNAP_DIR") ?? "/home/seb/Desktop/MoSimulator/unity-tests-971/cards";
        System.IO.Directory.CreateDirectory(dir);
        var cam = new GameObject("CardCam").AddComponent<Camera>();
        cam.fieldOfView = 42;
        var rt = new RenderTexture(1800, 1800, 24); cam.targetTexture = rt;
        var tex = new Texture2D(1800, 1800, TextureFormat.RGB24, false);
        // Frames the whole robot (plus an optional extra point, e.g. a coral being taken in) from a direction.
        void Frame(string name, Vector3 viewFrom, Vector3? extra = null, float margin = 1.15f)
        {
            var b = _robot.GetComponentsInChildren<MeshRenderer>().Where(r => r.enabled && !r.GetComponentInParent<PieceCtrl>())
                .Select(r => r.bounds).Where(x => x.size.magnitude < 5f && !float.IsNaN(x.center.x))
                .Aggregate((x, y) => { x.Encapsulate(y); return x; });
            L($"card {name} frame centre={b.center:F2} size={b.size:F2}");
            if (extra.HasValue) b.Encapsulate(extra.Value);
            var radius = b.extents.magnitude;
            var dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * margin * 0.8f;
            // Nothing between the camera and the robot (field trusses, stray game pieces) gets in the picture.
            cam.nearClipPlane = Mathf.Max(0.05f, dist - radius * 1.05f);
            Snap(name, b.center + viewFrom.normalized * dist, b.center);
            cam.nearClipPlane = 0.05f;
            L($"card {name} near={Mathf.Max(0.05f, dist - radius * 1.05f):F2} dist={dist:F2}");
        }
        void Snap(string name, Vector3 from, Vector3 at)
        {
            // A fresh camera each shot: something in the game changes the old one's settings after a piece is scored.
            var near = cam.nearClipPlane;
            Object.Destroy(cam.gameObject);
            cam = new GameObject("CardCam").AddComponent<Camera>();
            cam.fieldOfView = 42; cam.nearClipPlane = near; cam.targetTexture = rt;
            cam.transform.position = from; cam.transform.LookAt(at); cam.Render();
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1800, 1800), 0, 0); tex.Apply(); RenderTexture.active = null;
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
        }
        var reef = GameObject.Find(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? "BlueReef" : "RedReef").transform.position;
        reef.y = 0;
        // CARDS="score,ground,station,hero" picks which shots to take (default all).
        var want = (Environment.GetEnvironmentVariable("CARDS") ?? "ground,station,hero,score_front,score_back").Split(',');
        // Ground intake: the intake down and a coral being drawn in.
        if (want.Contains("ground"))
        {
        yield return DropHeld();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(1.2f);
        var gi = Field("groundCoralIntake") as ReefscapeGamePieceIntake;
        var box = Boxes(gi)[0].transform;
        var outward = Vector3.ProjectOnPlane(box.position - t.position, Vector3.up).normalized;
        var coral = FreePiece("Coral", t.position);
        Put(coral, new Vector3(box.position.x, 0.07f, box.position.z) + outward * 0.12f, Along(coral, t.right));
        yield return new WaitForSeconds(0.12f);
        var gfocus = t.position + outward * 0.4f + Vector3.up * 0.35f;
        Frame("intake_ground", outward * 1.2f + Vector3.Cross(Vector3.up, outward) * 1.0f + Vector3.up * 0.55f, coral.transform.position);
        yield return new WaitForSeconds(1.5f);
        Keys();
        }
        // Station intake: backed up to the coral station with a coral dropping in.
        if (want.Contains("station"))
        {
        yield return DropHeld();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        var station = Object.FindObjectsByType<Games.Reefscape.FieldScripts.CoralStation>(FindObjectsSortMode.None).First(st => st.Alliance == _robot.Alliance);
        var spawn = (Transform)typeof(Games.Reefscape.FieldScripts.CoralStation).GetField("coralSpawnpoint", F).GetValue(station);
        var sf = spawn.forward; sf.y = 0; sf.Normalize();
        PlaceRobot(spawn.position + sf * 0.8f, sf);
        yield return new WaitForSeconds(0.5f);
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(1.5f);
        var hpi = Field("hpCoralIntake") as ReefscapeGamePieceIntake;
        var hbox = Boxes(hpi)[0].transform;
        Put(FreePiece("Coral", hbox.position), hbox.position + Vector3.up * 0.25f - sf * 0.1f, hbox.rotation);
        yield return new WaitForSeconds(0.15f);
        var hfocus = t.position + Vector3.up * 0.8f - sf * 0.2f;
        var hside = Vector3.Cross(Vector3.up, sf);
        Frame("intake_station", hside * 1.4f + sf * 0.5f + Vector3.up * 0.45f, spawn.position);
        yield return new WaitForSeconds(1.5f);
        Keys();
        }
        // Hero shot.
        if (want.Contains("hero"))
        {
        PlaceRobot(new Vector3(-2.0f, 0, 0f), new Vector3(1, 0, 1));
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(2f);
        var c0 = t.position + Vector3.up * 0.75f;
        Frame("hero", t.forward * 1.3f + t.right * 0.9f + Vector3.up * 0.35f);
        }
        // The robot's main-menu model, as it is in its MainMenu prefab (placed on the field next to the robot).
        if (want.Contains("menu"))
        {
#if UNITY_EDITOR
            var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(Environment.GetEnvironmentVariable("MENU_PREFAB") ?? "Assets/Prefabs/Reefscape/Robots/Mods/Spartans/971/MainMenu971.prefab");
            var menu = Object.Instantiate(prefab, new Vector3(-2.0f, 0, -3.0f), Quaternion.identity);
            foreach (var body in menu.GetComponentsInChildren<Rigidbody>()) body.isKinematic = true;
            yield return new WaitForSeconds(0.5f);
            var mb = menu.GetComponentsInChildren<Renderer>().Where(x => x.enabled).Select(x => x.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; });
            L($"menu model bounds centre={mb.center:F2} size={mb.size:F2} renderers={menu.GetComponentsInChildren<Renderer>().Length}");
            var md = mb.extents.magnitude / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.95f;
            Snap("menu", mb.center + new Vector3(0.635f, 0.156f, 0.757f).normalized * md, mb.center); // the menu camera: 40 deg right of forward, 9 deg up
            Object.Destroy(menu);
#endif
        }
        // Close looks at both camera boxes and the end-effector spike.
        if (want.Contains("parts"))
        {
            PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1.5f);
            Vector3 Centre(string n) => _robot.GetComponentsInChildren<Renderer>(true).First(x => x.name == n).bounds.center;
            var far = Centre("Camera Box Hood.001");
            Snap("cam_nonintake", far + t.forward * 0.28f + t.right * 0.12f + Vector3.up * 0.08f, far);
            var near = Centre("Camera Box Hood");
            Snap("cam_intake", near - t.forward * 0.28f + t.right * 0.12f + Vector3.up * 0.08f, near);
            // Cut away everything nearer than the spike's own plane, with no coral in the claw.
            yield return DropHeld();
            yield return new WaitForSeconds(0.5f);
            var spike = Centre("Spike");
            cam.nearClipPlane = 0.27f;
            Snap("spike_cut", spike + t.right * 0.3f, spike);
            cam.nearClipPlane = 0.05f;
        }
        // Side views through the robot: the ground intake coming in, and the algae coming in, against the elevator.
        if (want.Contains("phase"))
        {
            var el = (Field("elevator") as Component).transform;
            var cols = _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
            var intakeCols = cols.Where(c => c.transform.IsChildOf((Field("intake") as Component).transform)).ToArray();
            var armRoot = (Field("arm") as Component).transform;
            var elevCols = cols.Where(c => c.name != "" && c.transform.GetComponentInParent<Rigidbody>() && !c.transform.IsChildOf((Field("intake") as Component).transform) && !c.transform.IsChildOf(armRoot) && c.transform.IsChildOf(_robot.transform) && c.transform.GetComponentsInParent<Transform>().Any(x => x.name is "Stage One" or "Stage Two" or "Elevator")).ToArray();
            string Pen(Collider[] a, Collider[] b)
            {
                var hits = new System.Collections.Generic.List<(string, float)>();
                foreach (var x in a) foreach (var y in b)
                    if (x.bounds.Intersects(y.bounds) && Physics.ComputePenetration(x, x.transform.position, x.transform.rotation, y, y.transform.position, y.transform.rotation, out _, out var dist) && dist > 0.002f)
                        hits.Add(($"{x.name}>{y.name}", dist));
                return string.Join(", ", hits.OrderByDescending(h => h.Item2).Take(5).Select(h => $"{h.Item1}:{h.Item2 * 100:F1}cm"));
            }
            PlaceRobot(new Vector3(-2.2f, 0, 2.2f), Vector3.forward);
            yield return DropHeld();
            if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(2f);
            var c0 = t.position + Vector3.up * 0.55f - t.forward * 0.05f;
            Vector3 Side(float cut) { cam.nearClipPlane = 2f - cut; return c0 + t.right * 2f; }
            void Both(string n) { Snap(n + "_cut30", Side(0.3f), c0); Snap(n + "_cut5", Side(0.05f), c0); cam.nearClipPlane = 0.05f; L($"phase {n} intake={(Field("intake") as GenericJoint).GetSingleAxisAngle(JointAxis.Y):F1} arm={(Field("arm") as GenericJoint).GetSingleAxisAngle(JointAxis.X):F1} intakeVsElevator=[{Pen(intakeCols, elevCols)}] elevCols={elevCols.Length}"); }
            Both("gi_down");
            Keys(Key.Z);
            var swing = new System.Collections.Generic.List<string>();
            var lastAng = 999f;
            var worst = 0f;
            var worstAt = "";
            for (var k = 0; k < 500; k++)
            {
                yield return new WaitForFixedUpdate();
                var ang = Mathf.DeltaAngle(0, (Field("intake") as GenericJoint).GetSingleAxisAngle(JointAxis.Y) + 71.4f);
                var pen = Pen(intakeCols, elevCols);
                if (Mathf.Abs(ang - lastAng) >= 8f)
                {
                    lastAng = ang;
                    swing.Add($"{k}:{ang:F0}deg[{pen}]");
                    Snap($"swing{ang:000}", Side(0.3f), c0);
                    Snap($"swing{ang:000}_mid", Side(0.05f), c0);
                    cam.nearClipPlane = 0.05f;
                }
                var depth = pen == "" ? 0f : float.Parse(pen.Split(',')[0].Split(':')[1].Replace("cm", ""), System.Globalization.CultureInfo.InvariantCulture);
                if (depth > worst) { worst = depth; worstAt = $"{ang:F1}deg {pen}"; }
            }
            L($"phase swing worst=[{worstAt}] frames {string.Join(" ", swing)}");
            Keys();
            Both("gi_in");
            yield return new WaitForSeconds(1f);
            Both("gi_stow");
            {
                var (mine, others) = IntakeAndElevatorMeshes();
                var best = 0f; var at = c0; var who = "";
                foreach (var m in mine)
                foreach (var o in others)
                {
                    if (!m.GetComponent<Renderer>().bounds.Intersects(o.GetComponent<Renderer>().bounds)) continue;
                    var depth = ObbDepth(m, o, out var mid);
                    if (depth > best && o.name.StartsWith("Base V Tube")) { best = depth; at = mid; who = $"{m.name}>{o.name}"; }
                }
                if (who == "") at = t.TransformPoint(new Vector3(-0.189f, 0.569f, -0.198f));
                L($"phase close-up on [{who}] depth={best * 100:F1}cm robotLocal={t.InverseTransformPoint(at):F3}");
                cam.nearClipPlane = 0.5f; Snap("close_side", at + t.right * 0.52f, at);
                Snap("close_otherside", at - t.right * 0.52f, at);
                cam.nearClipPlane = 0.05f; Snap("close_top", at + Vector3.up * 0.45f + t.right * 0.08f, at);
                Snap("close_angle", at - t.forward * 0.4f + t.right * 0.35f + Vector3.up * 0.2f, at);
            }
            // Algae coming in from each reef pose: the ball and the claw against the elevator, every step of the swing.
            yield return Tap(Key.Semicolon);
            var ai = Field("algaeIntake") as ReefscapeGamePieceIntake;
            var armJ = Field("arm") as GenericJoint;
            var (_, elevatorParts) = IntakeAndElevatorMeshes();
            var clawParts = armRoot.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh && m.GetComponent<Renderer>().enabled && !m.GetComponentInParent<PieceCtrl>()).ToArray();
            foreach (var (label, key, facing) in new[] { ("low", Key.I, true), ("high", Key.O, true), ("low", Key.I, false), ("high", Key.O, false) })
            {
                PlaceRobot(new Vector3(-2.2f, 0, 2.2f), facing ? Vector3.forward : Vector3.back);
                yield return Tap(key);
                yield return new WaitForSeconds(2f);
                Keys(Key.LeftShift);
                yield return new WaitForSeconds(0.2f);
                var ball = FreePiece("Algae", ai.transform.position);
                Put(ball, ((Transform)typeof(ReefscapeGamePieceIntake).BaseType.GetField("target", F).GetValue(ai)).position + ai.transform.forward * 0.1f, Quaternion.identity);
                for (var w = 0; w < 200 && !NodeOf("Algae").HasPiece(); w++) yield return new WaitForFixedUpdate();
                yield return new WaitForSeconds(0.6f);
                var name = $"{label}{(facing ? "Front" : "Back")}";
                var held = NodeOf("Algae").HasPiece();
                var ballR = ball.GetComponent<SphereCollider>() ? ball.GetComponent<SphereCollider>().bounds.extents.x : 0.203f;
                var ballWorst = (depth: 0f, who: "", arm: 0f);
                var clawWorst = (depth: 0f, who: "", arm: 0f);
                var lastArm = 999f;
                var frames = new System.Collections.Generic.List<string>();
                Keys(Key.Z);
                for (var k = 0; k < 400; k++)
                {
                    yield return new WaitForFixedUpdate();
                    if (k == 30) Keys();
                    var armA = armJ.GetSingleAxisAngle(JointAxis.X);
                    foreach (var o in elevatorParts)
                    {
                        var r = o.GetComponent<Renderer>().bounds;
                        // Ball against each part's oriented box.
                        var lp = o.transform.InverseTransformPoint(ball.transform.position) - o.sharedMesh.bounds.center;
                        var ext = o.sharedMesh.bounds.extents;
                        var nearest = new Vector3(Mathf.Clamp(lp.x, -ext.x, ext.x), Mathf.Clamp(lp.y, -ext.y, ext.y), Mathf.Clamp(lp.z, -ext.z, ext.z));
                        var gap = o.transform.TransformVector(lp - nearest).magnitude;
                        if (ballR - gap > ballWorst.depth) ballWorst = (ballR - gap, o.name, armA);
                    }
                    if (k % 4 == 0)
                        foreach (var m in clawParts)
                        {
                            var mb = m.GetComponent<Renderer>().bounds;
                            foreach (var o in elevatorParts)
                            {
                                if (!mb.Intersects(o.GetComponent<Renderer>().bounds)) continue;
                                var depth = ObbDepth(m, o, out _);
                                if (depth > clawWorst.depth) clawWorst = (depth, $"{m.name}>{o.name}", armA);
                            }
                        }
                    if (Mathf.Abs(Mathf.DeltaAngle(armA, lastArm)) >= 15f)
                    {
                        lastArm = armA;
                        frames.Add($"{k}:{armA:F0}");
                        var c1 = t.position + Vector3.up * 1.0f;
                        cam.nearClipPlane = 2.2f - 0.3f; Snap($"{name}_{armA:000}", c1 + t.right * 2.2f, c1);
                        cam.nearClipPlane = 2.2f - 0.03f; Snap($"{name}_{armA:000}_mid", c1 + t.right * 2.2f, c1);
                        cam.nearClipPlane = 0.05f;
                    }
                }
                L($"phase comingIn {name} held={held} ballInside=[{ballWorst.who} {ballWorst.depth * 100:F1}cm at arm {ballWorst.arm:F0}] clawInside=[{clawWorst.who} {clawWorst.depth * 100:F1}cm at arm {clawWorst.arm:F0}] frames {string.Join(" ", frames)}");
                yield return DropHeld();
            }
        }
        // A close look at the LED strips on the elevator uprights.
        if (want.Contains("leds"))
        {
            PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1.5f);
            var strips = _robot.GetComponentsInChildren<Transform>(true).Where(x => x.name.StartsWith("LED Strip (")).ToArray();
            var mid = strips.Aggregate(Vector3.zero, (a, x) => a + x.GetComponentInChildren<Renderer>().bounds.center) / Mathf.Max(1, strips.Length);
            foreach (var st in strips)
            {
                var rr = st.GetComponentInChildren<Renderer>();
                L($"led {st.name} enabled={rr.enabled}/{rr.gameObject.activeInHierarchy} centre(robot)={t.InverseTransformPoint(rr.bounds.center):F3} size={rr.bounds.size:F3} mat={rr.sharedMaterial?.name} shader={rr.sharedMaterial?.shader.name} layer={LayerMask.LayerToName(rr.gameObject.layer)}");
            }
            Snap("leds", mid - t.right * 1.3f + t.forward * 0.9f + Vector3.up * 0.3f, mid);
            var s0 = strips[0].GetComponentInChildren<Renderer>().bounds.center;
            Snap("leds_close", s0 - t.right * 0.6f + Vector3.up * 0.05f, s0);
            var s1 = strips[1].GetComponentInChildren<Renderer>().bounds.center;
            Snap("leds_close_end", s1 - t.forward * 0.6f + Vector3.up * 0.05f, s1);
        }
        // "wuh": the robot sitting inside the barge's net (held there for the picture), like 9483A's third card.
        if (want.Contains("wuh"))
        {
            var net = Object.FindObjectsByType<Games.Reefscape.Scoring.Scorers.BargeScorer>(FindObjectsSortMode.None)
                .Where(x => x.Alliance == _robot.Alliance).Select(x => x.GetComponent<BoxCollider>()).First(x => x);
            var nb = net.bounds;
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1.5f);
            var rb = _robot.GetComponent<Rigidbody>();
            var along = nb.size.x > nb.size.z ? Vector3.right : Vector3.forward;
            rb.isKinematic = true;
            PlaceRobot(new Vector3(nb.center.x, 0, nb.center.z), along);
            t.position = new Vector3(t.position.x, nb.min.y + 0.03f, t.position.z);
            Physics.SyncTransforms();
            yield return new WaitForSeconds(1.5f);
            var across = Vector3.Cross(Vector3.up, along);
            var focus = t.position + Vector3.up * 0.55f;
            cam.nearClipPlane = 0.05f;
            Snap("wuh", focus + across * 2.6f + along * 1.2f + Vector3.up * 0.9f, focus);
            rb.isKinematic = false;
        }
        // Scoring L4 facing the reef, then facing away.
        // (Last: after a coral is scored, later renders come out black.)
        foreach (var facing in new[] { true, false }.Where(f => want.Contains(f ? "score_front" : "score_back")))
        {
            yield return LoadCoral();
            var ang = Mathf.Deg2Rad * 210f;
            var d = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            PlaceRobot(reef + d * 1.75f, facing ? -d : d);
            yield return new WaitForSeconds(0.5f);
            Keys(Key.Q);
            yield return new WaitForSeconds(1.5f);
            yield return Tap(Key.P);
            yield return new WaitForSeconds(2.5f);
            Keys();
            var side = Vector3.Cross(Vector3.up, d);
            var focus = t.position + Vector3.up * 1.1f - d * 0.15f;
            Frame(facing ? "score_front" : "score_back", d * 1.1f + side * 1.3f + Vector3.up * 0.45f);
            yield return Tap(Key.K);
            yield return new WaitForSeconds(1.2f);
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1.5f);
        }
    }

    // ---- letting go of intake just as the ground intake starts pulling a coral in: it still ends up in the claw
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator GroundRelease()
    {
        yield return Boot();
        var t = _robot.transform;
        var rb = _robot.GetComponent<Rigidbody>();
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Coral) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        var gi = Field("groundCoralIntake") as ReefscapeGamePieceIntake;
        foreach (var holdAfter in new[] { 0f, 0.1f, 0.25f })
        {
            yield return DropHeld();
            PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
            yield return new WaitForSeconds(0.5f);
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(1.2f);
            var box = Boxes(gi)[0].transform;
            var outward = Vector3.ProjectOnPlane(box.position - t.position, Vector3.up).normalized;
            var coral = FreePiece("Coral", t.position);
            Put(coral, new Vector3(box.position.x, 0.07f, box.position.z) + outward * 0.25f, Along(coral, t.right));
            var e = 0f;
            for (; e < 4f && !gi.hasGamePiece && !NodeOf("Coral").HasPiece(); e += Time.fixedDeltaTime)
            {
                rb.AddForce(outward * 400f);
                yield return new WaitForFixedUpdate();
            }
            for (var h = 0f; h < holdAfter; h += Time.fixedDeltaTime) yield return new WaitForFixedUpdate();
            Keys(); // let go
            yield return new WaitForSeconds(2.5f);
            var held = NodeOf("Coral").HasPiece();
            var free = FreePiece("Coral", t.position);
            var where = free ? t.InverseTransformPoint(free.transform.position) : Vector3.zero;
            L($"RESULT groundRelease releasedAfter={holdAfter:F2}s (acquired at {e:F2}s) inClaw={held} nearestFreeCoral(robot-local)={where:F2} {Status()}");
        }
    }

    // ---- builds 971's main-menu model: the robot as it stands at L2 (facing the reef) holding a coral, stripped to its
    // meshes, LEDs in 9999's menu LED material, turned so the claw faces the single-player menu camera (which looks at the
    // robot from 40 deg right of its forward and ~9 deg above level), saved as MainMenuModel971.prefab.
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator BuildMenuModel()
    {
#if UNITY_EDITOR
        yield return Boot();
        var t = _robot.transform;
        yield return LoadCoral();
        // Facing this way the robot takes the L2 pose whose claw is on the side away from the ground intake, so with the
        // claw toward the camera the intake faces away.
        var reefPos = GameObject.Find(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? "BlueReef" : "RedReef").transform.position;
        reefPos.y = 0;
        var away = new Vector3(Mathf.Cos(Mathf.Deg2Rad * 210f), 0, Mathf.Sin(Mathf.Deg2Rad * 210f));
        PlaceRobot(reefPos + away * 1.75f, -away); // facing the reef: the front L2 pose
        yield return new WaitForSeconds(0.5f);
        yield return Tap(Key.I);
        yield return new WaitForSeconds(3f);
        Assert.IsTrue(NodeOf("Coral").HasPiece(), "no coral held for the menu model");
        var claw = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState).stateTarget;
        var clawDir = Vector3.ProjectOnPlane(claw.forward, Vector3.up).normalized; // the coral points out of the claw
        var intakeSide = t.InverseTransformPoint((Field("intake") as Component).transform.position);
        L($"menu model: L2 pose {Status()} clawDir(robot)={t.InverseTransformDirection(clawDir):F2} intake(robot)={intakeSide:F2}");

        const string dir = "Assets/Prefabs/Reefscape/Robots/Mods/Spartans/971";
        var ledMat = $"{dir}/Materials/Coral Led.mat";
        if (!AssetDatabase.LoadAssetAtPath<Material>(ledMat)) AssetDatabase.CopyAsset("Assets/Prefabs/Reefscape/Robots/Mods/Lanternfly/9999/Coral Led.mat", ledMat);
        var led = AssetDatabase.LoadAssetAtPath<Material>(ledMat);

        var model = Object.Instantiate(_robot.gameObject, t.position, t.rotation);
        model.name = "971";
        // Strip everything but meshes: scripts first (some require the physics parts), then joints, bodies, colliders, sound.
        foreach (var c in model.GetComponentsInChildren<MonoBehaviour>(true).Where(c => c && !c.GetType().Name.StartsWith("TextMeshPro")).ToArray()) Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Joint>(true).ToArray()) Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Rigidbody>(true).ToArray()) Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Collider>(true).ToArray()) Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<AudioSource>(true).ToArray()) Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Animator>(true).ToArray()) Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Camera>(true).ToArray()) Object.DestroyImmediate(c);
        foreach (var c in model.GetComponentsInChildren<Light>(true).ToArray()) Object.DestroyImmediate(c);
        // Renderers with no mesh left behind (e.g. trails) and anything that isn't a saved asset.
        int ledCount = 0, fixedMats = 0;
        foreach (var r in model.GetComponentsInChildren<Renderer>(true))
        {
            var isLed = r.transform.parent && r.transform.parent.name == "LED Strip";
            var mats = r.sharedMaterials;
            for (var i = 0; i < mats.Length; i++)
            {
                if (isLed) { mats[i] = led; continue; }
                if (mats[i] && !AssetDatabase.Contains(mats[i]))
                {
                    var asset = AssetDatabase.FindAssets(mats[i].name.Replace(" (Instance)", "") + " t:Material").Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault();
                    L($"menu model: runtime material '{mats[i].name}' on {r.name} -> {asset ?? "none"}");
                    mats[i] = asset != null ? AssetDatabase.LoadAssetAtPath<Material>(asset) : null;
                    fixedMats++;
                }
            }
            r.sharedMaterials = mats;
            if (isLed) ledCount++;
        }
        // Face the claw toward the menu camera: 40 deg to the right of the model's forward.
        var clawLocal = t.InverseTransformDirection(clawDir);
        var have = Mathf.Atan2(clawLocal.x, clawLocal.z) * Mathf.Rad2Deg;
        model.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0, 40f - have, 0));
        var path = $"{dir}/MainMenuModel971.prefab";
        PrefabUtility.SaveAsPrefabAsset(model, path, out var ok);
        L($"RESULT menuModel saved={ok} renderers={model.GetComponentsInChildren<Renderer>(true).Length} leds={ledCount} runtimeMaterialsReplaced={fixedMats} clawYaw={have:F1} turned={40f - have:F1}");
        Object.DestroyImmediate(model);
#else
        yield break;
#endif
    }

    // ---- mashing inputs: the arm must never swing down through the robot (its angle from straight up stays < 170 deg)
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator ArmSpam()
    {
        yield return Boot();
        var t = _robot.transform;
        var arm = (Field("arm") as Component).transform;
        var up0 = t.InverseTransformDirection(arm.up);
        PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
        var keys = new[] { Key.U, Key.I, Key.O, Key.P, Key.Z, Key.LeftShift, Key.Semicolon, Key.V, Key.K };
        var rng = new System.Random(971);
        float worst = 0;
        for (var e = 0f; e < 25f;)
        {
            var k = keys[rng.Next(keys.Length)];
            if (k == Key.LeftShift) Keys(Key.LeftShift); else { Keys(); yield return Tap(k); }
            var hold = 0.05f + (float)rng.NextDouble() * 0.35f;
            for (var h = 0f; h < hold; h += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                worst = Mathf.Max(worst, Vector3.Angle(t.InverseTransformDirection(arm.up), up0));
            }
            e += hold + 0.1f;
        }
        Keys();
        L($"RESULT armSpam maxArmFromStow={worst:F1}deg (hard stop 165, L1 poses 153)");
    }

    // ---- side-to-side wobble: the claw's sideways sway (robot frame) while the robot drives, spins and stops
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator SideWobble()
    {
        yield return Boot();
        var t = _robot.transform;
        var rb = _robot.GetComponent<Rigidbody>();
        var claw = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        var arm = (Field("arm") as Component).transform;
        yield return LoadCoral();
        foreach (var (pose, key) in new[] { ("stow", Key.Z), ("L4", Key.P), ("L2", Key.I) })
        {
            PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
            yield return Tap(key);
            yield return new WaitForSeconds(2.5f);
            var x0 = t.InverseTransformPoint(claw.position).x;
            float maxDev = 0, settleDev = 0, maxTwist = 0;
            var axis0 = t.InverseTransformDirection(arm.right);
            for (var e = 0f; e < 4f; e += Time.fixedDeltaTime)
            {
                // 1 s drive sideways, 1 s spin, then stop and watch it settle.
                if (e < 1f) rb.AddForce(t.right * 900f);
                else if (e < 2f) rb.AddTorque(Vector3.up * 400f);
                yield return new WaitForFixedUpdate();
                var dev = Mathf.Abs(t.InverseTransformPoint(claw.position).x - x0);
                maxDev = Mathf.Max(maxDev, dev);
                if (e > 3f) settleDev = Mathf.Max(settleDev, dev);
                // How far the arm's pivot axis has twisted away from the robot's (it should stay parallel).
                maxTwist = Mathf.Max(maxTwist, Vector3.Angle(t.InverseTransformDirection(arm.right), axis0));
            }
            L($"RESULT sideWobble {pose} clawSwayMax={maxDev * 100:F2}cm afterStop={settleDev * 100:F2}cm armAxisTwistMax={maxTwist:F2}deg");
        }
    }

    // ---- arm and wrist wobble under real full-speed driving: twist off the hinge axis (sideways play) and hinge
    // ringing (peak-to-peak of the joint angle), at several poses
    [UnityTest]
    [Timeout(600000)]
    public IEnumerator ArmWobble()
    {
        yield return Boot();
        var t = _robot.transform;
        var claw = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        var arm = Field("arm") as GenericJoint;
        var wrist = Field("wrist") as GenericJoint;
        var armParent = arm.GetComponent<ConfigurableJoint>().connectedBody.transform;
        var logs = new[] { arm.gameObject.AddComponent<ContactLog>(), wrist.gameObject.AddComponent<ContactLog>() };
        yield return LoadCoral();
        // Drive phase: full-speed strafes and reversals (the hinge must hold its angle). Spin phase: the arm may swap
        // front/back as the reef passes, so only the off-hinge twist and the sway count there.
        var drive = new (Key key, float s, bool spin)[] { (Key.A, 0.8f, false), (Key.D, 0.8f, false), (Key.W, 0.8f, false), (Key.S, 0.8f, false), (Key.J, 1f, true), (Key.L, 1f, true) };
        foreach (var (pose, key) in new[] { ("stow", Key.Z), ("L4", Key.P), ("L2", Key.I), ("L1", Key.U), ("L3", Key.O) })
        {
            PlaceRobot(new Vector3(-2.0f, 0, 0f), Vector3.forward);
            Keys();
            yield return Tap(key);
            yield return new WaitForSeconds(3f);
            var x0 = t.InverseTransformPoint(claw.position).x;
            foreach (var l in logs) l.hits.Clear();
            var stage0 = Quaternion.Inverse(t.rotation) * armParent.rotation;
            var m = new float[2, 4]; // [drive, spin] x [arm off-axis, wrist off-axis, claw sway, carriage twist]
            float aMin = 999, aMax = -999, wMin = 999, wMax = -999, aS = 0, wS = 0;
            float a0 = arm.GetSingleAxisAngle(JointAxis.X), w0 = wrist.GetSingleAxisAngle(JointAxis.X);
            void Sample(int ph)
            {
                m[ph, 0] = Mathf.Max(m[ph, 0], Vector3.Angle(arm.transform.right, armParent.right));
                m[ph, 1] = Mathf.Max(m[ph, 1], Vector3.Angle(wrist.transform.right, arm.transform.right));
                m[ph, 2] = Mathf.Max(m[ph, 2], Mathf.Abs(t.InverseTransformPoint(claw.position).x - x0));
                m[ph, 3] = Mathf.Max(m[ph, 3], Quaternion.Angle(stage0, Quaternion.Inverse(t.rotation) * armParent.rotation));
            }
            foreach (var (k, s, spin) in drive)
            {
                Keys(k);
                for (var e = 0f; e < s; e += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    Sample(spin ? 1 : 0);
                    if (spin) continue;
                    var a = Mathf.DeltaAngle(a0, arm.GetSingleAxisAngle(JointAxis.X)); var w = Mathf.DeltaAngle(w0, wrist.GetSingleAxisAngle(JointAxis.X));
                    aMin = Mathf.Min(aMin, a); aMax = Mathf.Max(aMax, a); wMin = Mathf.Min(wMin, w); wMax = Mathf.Max(wMax, w);
                }
            }
            Keys();
            yield return new WaitForSeconds(1f);
            float a1 = arm.GetSingleAxisAngle(JointAxis.X), w1 = wrist.GetSingleAxisAngle(JointAxis.X);
            for (var e = 0f; e < 1f; e += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                aS = Mathf.Max(aS, Mathf.Abs(Mathf.DeltaAngle(a1, arm.GetSingleAxisAngle(JointAxis.X))));
                wS = Mathf.Max(wS, Mathf.Abs(Mathf.DeltaAngle(w1, wrist.GetSingleAxisAngle(JointAxis.X))));
            }
            L($"RESULT armWobble {pose} drive: armOffAxis={m[0, 0]:F2} wristOffAxis={m[0, 1]:F2}deg sway={m[0, 2] * 100:F2}cm carriage={m[0, 3]:F2}deg " +
              $"armHinge={aMin:F1}..{aMax:F1} wristHinge={wMin:F1}..{wMax:F1}deg | spin: armOffAxis={m[1, 0]:F2} wristOffAxis={m[1, 1]:F2}deg " +
              $"sway={m[1, 2] * 100:F2}cm carriage={m[1, 3]:F2}deg | stopped: arm={aS:F2} wrist={wS:F2}deg");
            foreach (var l in logs)
                foreach (var kv in l.hits.OrderByDescending(kv => kv.Value.impulse).Take(6))
                    L($"armWobble {pose} {l.name} contact {kv.Key} steps={kv.Value.n} maxImpulse={kv.Value.impulse:F2} robotAt={t.position:F2}");
        }
    }

    // ---- player notes, driven with the real keys. Keys that move the robot along a field direction (they are field-centric).
    Key[] DriveKeys(Vector3 dir)
    {
        var keys = new System.Collections.Generic.List<Key>();
        if (Mathf.Abs(dir.x) > 0.3f) keys.Add(dir.x > 0 ? Key.S : Key.W);
        if (Mathf.Abs(dir.z) > 0.3f) keys.Add(dir.z > 0 ? Key.D : Key.A);
        return keys.ToArray();
    }

    Vector3 ReefCentre()
    {
        var reef = GameObject.Find(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? "BlueReef" : "RedReef").transform.position;
        reef.y = 0;
        return reef;
    }

    // Placing, then driving off after a short wait: does the coral stay on the branch? (level, facing the reef, wait
    // after the place, direction of travel, stow while leaving)
    [UnityTest]
    [Timeout(1200000)]
    public IEnumerator PlaceThenDrive()
    {
        yield return Boot();
        var reef = ReefCentre();
        ClearReefAlgae();
        var i = 0;
        foreach (var (name, key, facing, wait, move, stow) in new[]
                 {
                     ("L4", Key.P, true, 0f, "strafe", false), ("L4", Key.P, true, 0f, "strafe", false), ("L4", Key.P, false, 0f, "strafe", false), ("L4", Key.P, false, 0f, "strafe", false),
                     ("L4", Key.P, true, 0f, "spin", false), ("L4", Key.P, true, 0f, "spin", false), ("L4", Key.P, false, 0f, "spin", false), ("L4", Key.P, false, 0f, "spin", false),
                     ("L4", Key.P, true, 0f, "away", false), ("L4", Key.P, true, 0f, "away", true), ("L3", Key.O, true, 0f, "strafe", false), ("L2", Key.I, true, 0f, "strafe", false),
                 })
        {
            ClearFloorCoral();
            yield return LoadCoral();
            var ang = Mathf.Deg2Rad * (60f * (i % 6) + 30f);
            var d = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            var align = i / 6 % 2 == 0 ? Key.Q : Key.E;
            i++;
            PlaceRobot(reef + d * 1.75f, facing ? -d : d);
            yield return new WaitForSeconds(0.5f);
            Keys(align);
            yield return new WaitForSeconds(2f);
            yield return Tap(key);
            yield return new WaitForSeconds(2.5f);
            var before = Score();
            var coral = NodeOf("Coral").controller;
            Keys(align, Key.K);
            for (var k = 0; k < 6; k++) yield return new WaitForFixedUpdate();
            Keys(align);
            var armed = !NodeOf("Coral").HasPiece();
            yield return new WaitForSeconds(wait);
            var dir = move == "strafe" ? Vector3.Cross(Vector3.up, d) : d;
            Keys(move == "spin" ? new[] { Key.J } : DriveKeys(dir));
            if (stow) yield return Tap(Key.Z);
            var rb = _robot.GetComponent<Rigidbody>();
            var speed = 0f;
            var maxImpulse = 0f;
            var cl = coral ? coral.gameObject.AddComponent<ContactLog>() : null;
            for (var e = 0f; e < 0.7f; e += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                speed = Mathf.Max(speed, Vector3.Dot(rb.velocity, dir));
            }
            Keys();
            yield return new WaitForSeconds(1.5f);
            var after = Score();
            var robotBodies = _robot.GetComponentsInChildren<Rigidbody>().Select(b => $"({b.name})").ToArray();
            var hits = cl ? cl.hits.Where(kv => robotBodies.Any(kv.Key.EndsWith)).OrderByDescending(kv => kv.Value.impulse).Take(3)
                .Select(kv => $"{kv.Key} n={kv.Value.n} imp={kv.Value.impulse:F1}") : Enumerable.Empty<string>();
            if (cl) Object.Destroy(cl);
            L($"RESULT placeDrive {name} {(facing ? "front" : "back")} wait={wait:F2}s {move}{(stow ? "+stow" : "")} released={armed} coralScored+={after.coral - before.coral} " +
              $"points+={after.pts - before.pts} speed={speed:F1} coralY={(coral ? coral.transform.position.y : -1):F2} hits=[{string.Join("; ", hits)}]");
        }
    }

    // Placing soon after the robot looks aligned, or soon after asking for the level. (level, facing, wait after arriving)
    [UnityTest]
    [Timeout(1200000)]
    public IEnumerator PlaceSoon()
    {
        yield return Boot();
        var reef = ReefCentre();
        ClearReefAlgae();
        var aa = _robot.GetComponent<ReefscapeAutoAlign>();
        var rb = _robot.GetComponent<Rigidbody>();
        var arm = Field("arm") as GenericJoint;
        var i = 0;
        foreach (var (name, key) in new[] { ("L4", Key.P), ("L3", Key.O) })
        foreach (var (mode, wait) in new[] { ("arrive", 0f), ("arrive", 0f), ("arrive", 0f), ("arrive", 0f), ("arrive", 0.05f), ("level", 0.3f) })
        {
            ClearFloorCoral();
            yield return LoadCoral();
            var ang = Mathf.Deg2Rad * (60f * (i % 6) + 30f);
            var d = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            var align = i / 6 % 2 == 0 ? Key.Q : Key.E;
            i++;
            var waited = 0f;
            if (mode == "arrive")
            {
                // At the level first, then drive in on the align and place once the robot has arrived.
                PlaceRobot(reef + d * 2.4f, -d);
                yield return new WaitForSeconds(0.3f);
                yield return Tap(key);
                yield return new WaitForSeconds(2.5f);
                Keys(align);
                for (var e = 0f; e < 4f; e += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    waited = e;
                    if (e > 0.2f && aa.getDistance() < 0.03f) break;
                }
            }
            else
            {
                // Aligned first, then ask for the level and place while it is still getting there.
                PlaceRobot(reef + d * 1.75f, -d);
                yield return new WaitForSeconds(0.3f);
                Keys(align);
                yield return new WaitForSeconds(2f);
                Keys(align, key);
                yield return new WaitForSeconds(0.1f);
                Keys(align);
            }
            yield return new WaitForSeconds(wait);
            var before = Score();
            var coral = NodeOf("Coral").controller;
            var v = rb.velocity.magnitude;
            var residual = aa.getDistance();
            var armAngle = arm ? arm.GetSingleAxisAngle(JointAxis.X) : 0;
            Keys(align, Key.K);
            for (var k = 0; k < 6; k++) yield return new WaitForFixedUpdate();
            Keys(align);
            yield return new WaitForSeconds(2.5f);
            var after = Score();
            Keys();
            L($"RESULT placeSoon {name} {mode} wait={wait:F2}s arriveAfter={waited:F2}s atPlace: robotSpeed={v:F2} residual={residual * 100:F1}cm arm={armAngle:F1} released={!NodeOf("Coral").HasPiece()} " +
              $"coralScored+={after.coral - before.coral} points+={after.pts - before.pts} coralY={(coral ? coral.transform.position.y : -1):F2}");
            yield return DropHeld();
        }
    }

    // Driving onto a coral on the carpet with the real keys, across the intake's width and at several angles.
    [UnityTest]
    [Timeout(1200000)]
    public IEnumerator GroundSweep()
    {
        yield return Boot();
        var t = _robot.transform;
        yield return DropHeld();
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Coral) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        var start = new Vector3(-2.2f, 0, 2.2f);
        var gi = Field("groundCoralIntake") as ReefscapeGamePieceIntake;
        var bodies = _robot.GetComponentsInChildren<Rigidbody>().Select(b => $"({b.name})").ToArray();
        foreach (var angle in new[] { 0f, 30f })
        foreach (var lateral in new[] { 0.2f, -0.2f, 0.27f, -0.27f, 0.32f })
        {
            yield return DropHeld();
            yield return Tap(Key.Z);
            PlaceRobot(start, Vector3.forward);
            yield return new WaitForSeconds(1f);
            var coral = FreePiece("Coral", start);
            Assert.NotNull(coral, "no free coral");
            // Intake on the robot's -z side; angle 0 lies across the intake, 90 points at it.
            var spot = start + Vector3.right * lateral + Vector3.back * 1.4f + Vector3.up * 0.06f;
            Put(coral, spot, Quaternion.Euler(0, 90f + angle, 90f) );
            Keys(Key.LeftShift);
            yield return new WaitForSeconds(0.2f);
            var clog = coral.gameObject.AddComponent<ContactLog>();
            Keys(Key.LeftShift, Key.A);
            var startTime = Time.time;
            var held = -1f;
            var stall = 0f;
            var longest = 0f;
            for (var e = 0f; e < 2.5f; e += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                if (e > 1.3f && _held.Contains(Key.A)) Keys(Key.LeftShift); // a driver stops once over it
                var crb = coral.GetComponent<Rigidbody>();
                var near = (coral.transform.position - t.position).magnitude < 0.8f;
                stall = near && crb && crb.velocity.magnitude < 0.05f ? stall + Time.fixedDeltaTime : 0;
                longest = Mathf.Max(longest, stall);
                if (NodeOf("Coral").HasPiece()) { held = Time.time - startTime; break; }
            }
            var end = t.InverseTransformPoint(coral.transform.position);
            var box = Boxes(gi)[0].transform;
            var boxLocal = box.InverseTransformPoint(coral.transform.position);
            var state = $"req={gi.requestIntake} has={gi.hasGamePiece} sec={gi.securedGamePiece} coralInBox(local)={boxLocal:F2} box(robot)={t.InverseTransformPoint(box.position):F2}";
            var touching = clog.hits.Where(kv => bodies.Any(kv.Key.EndsWith)).OrderByDescending(kv => kv.Value.n).Take(5).Select(kv => $"{kv.Key} n={kv.Value.n} imp={kv.Value.impulse:F1}");
            Object.Destroy(clog);
            Keys();
            yield return new WaitForSeconds(0.3f);
            L($"RESULT groundSweep angle={angle:F0} lateral={lateral:F2} held={held > 0} after={held:F2}s longestStall={longest:F2}s coral(robot-local)={end:F2} {state} touching=[{string.Join("; ", touching)}]");
        }
    }

    // Taking algae from the reef with coral on the levels that face allows (low algae blocks L2, high blocks L3): which
    // coral move, and what touched them? Drives in on the centre align, and straight in without it.
    [UnityTest]
    [Timeout(1200000)]
    public IEnumerator AlgaeKnock()
    {
        yield return Boot();
        var reef = ReefCentre();
        var t = _robot.transform;
        var faces = GameObject.FindGameObjectsWithTag("ReefFace").Where(f => Vector3.ProjectOnPlane(f.transform.position - reef, Vector3.up).magnitude < 2f).ToArray();
        var reefAlgae = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == "Algae" && p.transform.position.y > 0.4f && Vector3.ProjectOnPlane(p.transform.position - reef, Vector3.up).magnitude < 1.6f).ToArray();
        var midY = (reefAlgae.Min(p => p.transform.position.y) + reefAlgae.Max(p => p.transform.position.y)) / 2;
        L($"algaeKnock faces={faces.Length} reefAlgae={reefAlgae.Length} heights={string.Join(",", reefAlgae.Select(p => p.transform.position.y.ToString("F2")))}");
        var robotBodies = _robot.GetComponentsInChildren<Rigidbody>().Select(b => $"({b.name})").ToArray();
        var trial = 0;
        // (facing the reef, sideways offset from the face's centre when driving straight in; 0 = on the centre align)
        foreach (var (facing, offset) in new[] { (true, 0.1f), (true, 0f), (false, 0f), (false, 0f), (true, 0.2f), (true, 0f) })
        {
            var centre = offset == 0;
            var face = faces[trial % faces.Length].transform;
            var d = Vector3.ProjectOnPlane(face.position - reef, Vector3.up).normalized;
            trial++;
            var algae = reefAlgae.OrderBy(p => Vector3.ProjectOnPlane(p.transform.position - face.position, Vector3.up).sqrMagnitude).First();
            var low = algae.transform.position.y < midY;
            ClearFloorCoral();
            var levels = low ? new[] { ("L3", Key.O), ("L4", Key.P) } : new[] { ("L2", Key.I), ("L4", Key.P) };
            var scored = new System.Collections.Generic.List<(string label, PieceCtrl piece, Vector3 pos)>();
            foreach (var (name, key) in levels)
            foreach (var pole in new[] { Key.Q, Key.E })
            {
                yield return LoadCoral();
                PlaceRobot(reef + d * 1.75f, -d);
                yield return new WaitForSeconds(0.3f);
                Keys(pole);
                yield return new WaitForSeconds(2f);
                yield return Tap(key);
                yield return new WaitForSeconds(2.5f);
                var coral = NodeOf("Coral").controller;
                Keys(pole, Key.K);
                yield return new WaitForSeconds(1.2f);
                Keys();
                yield return Tap(Key.Z);
                PlaceRobot(reef + d * 2.6f, -d);
                yield return new WaitForSeconds(1.2f);
                if (coral) scored.Add(($"{name}{pole}", coral, coral.transform.position));
            }
            var seated = scored.Where(x => x.pos.y > 0.4f).Select(x => (x.label, x.piece, x.pos, log: x.piece.gameObject.AddComponent<ContactLog>())).ToArray();
            var score0 = Score();
            yield return Tap(Key.Semicolon);
            yield return Tap(low ? Key.I : Key.O);
            PlaceRobot(reef + d * 2.2f + Vector3.Cross(Vector3.up, d) * offset, facing ? -d : d);
            yield return new WaitForSeconds(2.2f);
            Keys(new[] { Key.LeftShift }.Concat(centre ? new[] { Key.Q, Key.E } : DriveKeys(-d)).ToArray());
            var got = -1f;
            var closest = 99f;
            for (var e = 0f; e < 3f; e += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                closest = Mathf.Min(closest, Vector3.Dot(t.position - reef, d));
                if (NodeOf("Algae").HasPiece()) { got = e; break; }
                // Like a driver: creep in once the arm is near the reef rather than ramming it.
                if (!centre) Keys(Vector3.Dot(t.position - reef, d) < 1.42f ? new[] { Key.LeftShift } : new[] { Key.LeftShift }.Concat(DriveKeys(-d)).ToArray());
            }
            var heldAlgae = NodeOf("Algae").HasPiece();
            var atGrab = seated.Count(x => x.piece && (x.piece.transform.position - x.pos).magnitude > 0.1f);
            // Back away with the algae, intake still held like a driver.
            Keys(new[] { Key.LeftShift }.Concat(DriveKeys(d)).ToArray());
            yield return new WaitForSeconds(0.7f);
            Keys();
            yield return new WaitForSeconds(1.2f);
            var moved = seated.Select(x =>
            {
                var hits = x.log ? x.log.hits.Where(kv => robotBodies.Any(kv.Key.EndsWith)).OrderByDescending(kv => kv.Value.impulse).Take(2).Select(kv => $"{kv.Key} imp={kv.Value.impulse:F1}") : Enumerable.Empty<string>();
                return $"{x.label}:{(x.piece ? (x.piece.transform.position - x.pos).magnitude * 100 : -1):F0}cm<{string.Join(",", hits)}>";
            });
            var fell = seated.Count(x => x.piece && (x.piece.transform.position - x.pos).magnitude > 0.1f);
            foreach (var x in seated) if (x.log) Object.Destroy(x.log);
            L($"RESULT algaeKnock trial={trial} {(low ? "low" : "high")} {(facing ? "front" : "back")} {(centre ? "centreAlign" : $"straight off={offset:F1}")} algae={heldAlgae} got={got:F2}s closest={closest:F2}m seated={seated.Length}/{scored.Count} knockedAtGrab={atGrab} knocked={fell} [{string.Join(" ", moved)}] coralScore {score0.coral}->{Score().coral}");
            yield return DropHeld();
            yield return Tap(Key.Semicolon);
            yield return Tap(Key.Z);
            // The coral go back for the next trial: the field only has so many.
            var spot = 0;
            foreach (var x in scored)
                if (x.piece) Put(x.piece, new Vector3(-3.4f, 0.1f, 1.2f + 0.45f * spot++), Quaternion.identity);
            L($"algaeKnock spare coral now {Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Count(p => p.PieceName == "Coral" && p.gameObject.activeInHierarchy && Vector3.ProjectOnPlane(p.transform.position - reef, Vector3.up).magnitude > 2.5f)}");
        }
    }

    // The algae align: from off to one side and turned, one button (or both) and intake. Coral sits on the levels that face
    // allows. (Where the robot ends up, whether it got the algae, what the claw did to the coral.)
    [UnityTest]
    [Timeout(1500000)]
    public IEnumerator AlgaeAlign()
    {
        yield return Boot();
        var reef = ReefCentre();
        var t = _robot.transform;
        var faces = GameObject.FindGameObjectsWithTag("ReefFace").Where(f => Vector3.ProjectOnPlane(f.transform.position - reef, Vector3.up).magnitude < 2f).ToArray();
        var reefAlgae = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == "Algae" && p.transform.position.y > 0.4f && Vector3.ProjectOnPlane(p.transform.position - reef, Vector3.up).magnitude < 1.6f)
            .Select(p => (piece: p, pos: p.transform.position, rot: p.transform.rotation)).ToArray();
        var midY = (reefAlgae.Min(p => p.pos.y) + reefAlgae.Max(p => p.pos.y)) / 2;
        var bodies = _robot.GetComponentsInChildren<Rigidbody>().Select(b => $"({b.name})").ToArray();
        var trial = 0;
        foreach (var (facing, keys, side) in new[] { (true, new[] { Key.Q }, 0.45f), (false, new[] { Key.E }, -0.45f), (true, new[] { Key.Q, Key.E }, -0.3f) })
        foreach (var wantLow in new[] { true, false })
        foreach (var _ in new[] { 0, 1 })
        {
            var face = faces.OrderBy(f => trial).Select((f, i) => (f, i)).Where(x =>
            {
                var a = reefAlgae.OrderBy(p => Vector3.ProjectOnPlane(p.pos - x.f.transform.position, Vector3.up).sqrMagnitude).First();
                return a.pos.y < midY == wantLow;
            }).Skip(trial % 3).First().f.transform;
            trial++;
            var d = Vector3.ProjectOnPlane(face.position - reef, Vector3.up).normalized;
            var across = Vector3.Cross(Vector3.up, d);
            var algae = reefAlgae.OrderBy(p => Vector3.ProjectOnPlane(p.pos - face.position, Vector3.up).sqrMagnitude).First();
            algae.piece.gameObject.SetActive(true);
            Put(algae.piece, algae.pos, algae.rot);
            ClearFloorCoral();
            var scored = new System.Collections.Generic.List<(string label, PieceCtrl piece, Vector3 pos)>();
            foreach (var (name, key) in wantLow ? new[] { ("L3", Key.O), ("L4", Key.P) } : new[] { ("L2", Key.I), ("L4", Key.P) })
            foreach (var pole in new[] { Key.Q, Key.E })
            {
                yield return LoadCoral();
                PlaceRobot(reef + d * 1.75f, -d);
                yield return new WaitForSeconds(0.3f);
                Keys(pole);
                yield return new WaitForSeconds(2f);
                yield return Tap(key);
                yield return new WaitForSeconds(2.5f);
                var coral = NodeOf("Coral").controller;
                Keys(pole, Key.K);
                yield return new WaitForSeconds(1.2f);
                Keys();
                yield return Tap(Key.Z);
                PlaceRobot(reef + d * 2.6f, -d);
                yield return new WaitForSeconds(1.2f);
                if (coral) scored.Add(($"{name}{pole}", coral, coral.transform.position));
            }
            var seated = scored.Where(x => x.pos.y > 0.4f).Select(x => (x.label, x.piece, x.pos, log: x.piece.gameObject.AddComponent<ContactLog>())).ToArray();
            yield return Tap(Key.Semicolon);
            yield return Tap(wantLow ? Key.I : Key.O);
            // Off to one side, a little too far back and turned, as a driver arrives.
            PlaceRobot(reef + d * 2.3f + across * side, Quaternion.Euler(0, facing ? 20f : -20f, 0) * (facing ? -d : d));
            yield return new WaitForSeconds(2.2f);
            var ballMoved = (algae.piece.transform.position - algae.pos).magnitude;
            var ai = Field("algaeIntake") as ReefscapeGamePieceIntake;
            var wheels = Field("clawRollers") as Component[];
            var ballRb = algae.piece.GetComponent<Rigidbody>();
            float WheelGap() => wheels.Min(w => (w.transform.position - algae.piece.transform.position).magnitude) - 0.203f - 0.054f;
            Keys(new[] { Key.LeftShift }.Concat(keys).ToArray());
            var got = -1f;
            var pullAt = -1f;
            var gapAtPull = 99f;
            float clawToBall = 99f, ballFromFace = 99f, clawFromFace = 99f, robotFromFace = 99f;
            var robotSpeedAtPull = 0f;
            var ballPeak = 0f;
            var phases = new System.Collections.Generic.List<string>();
            var lastSpeedBand = -1;
            for (var e = 0f; e < 6f; e += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                if (pullAt < 0 && ai.requestIntake) { pullAt = e; clawToBall = t.InverseTransformPoint(algae.piece.transform.position).x - t.InverseTransformPoint(ai.transform.position).x; ballFromFace = Vector3.Dot(algae.piece.transform.position - face.position, across); clawFromFace = Vector3.Dot(ai.transform.position - face.position, across); robotFromFace = Vector3.Dot(t.position - face.position, across); gapAtPull = WheelGap(); robotSpeedAtPull = _robot.GetComponent<Rigidbody>().velocity.magnitude; }
                if (ballRb) ballPeak = Mathf.Max(ballPeak, ballRb.velocity.magnitude);
                var band = Mathf.RoundToInt(_robot.GetComponent<Rigidbody>().velocity.magnitude * 5);
                if (band != lastSpeedBand && phases.Count < 14) { lastSpeedBand = band; phases.Add($"{e:F2}:{band / 5f:F1}@{Vector3.Dot(t.position - reef, d):F2}"); }
                if (got < 0 && NodeOf("Algae").HasPiece()) got = e;
                if (got >= 0 && e > got + 0.8f) break;
            }
            var ballGap = gapAtPull * 100;
            var endDist = Vector3.Dot(t.position - reef, d);
            var endSide = Vector3.Dot(t.position - reef, across);
            var faceYaw = Vector3.Angle(Vector3.ProjectOnPlane(t.forward, Vector3.up), facing ? -d : d);
            var speed = _robot.GetComponent<Rigidbody>().velocity.magnitude;
            var heldAlgae = NodeOf("Algae").HasPiece();
            Keys(new[] { Key.LeftShift }.Concat(DriveKeys(d)).ToArray());
            yield return new WaitForSeconds(0.7f);
            Keys();
            yield return new WaitForSeconds(1.2f);
            var worst = 0f;
            var moved = seated.Select(x =>
            {
                var hit = x.log ? x.log.hits.Where(kv => bodies.Any(kv.Key.EndsWith)).Select(kv => kv.Value.impulse).DefaultIfEmpty(0).Max() : 0;
                worst = Mathf.Max(worst, hit);
                return $"{x.label}:{(x.piece ? (x.piece.transform.position - x.pos).magnitude * 100 : -1):F0}cm";
            }).ToArray();
            var fell = seated.Count(x => x.piece && (x.piece.transform.position - x.pos).magnitude > 0.1f);
            var nudged = seated.Count(x => x.piece && (x.piece.transform.position - x.pos).magnitude > 0.01f);
            foreach (var x in seated) if (x.log) Object.Destroy(x.log);
            L($"RESULT algaeAlign {(wantLow ? "low" : "high")} {(facing ? "front" : "back")} keys={string.Join("+", keys)} algae={heldAlgae} got={got:F2}s ballMovedBefore={ballMoved * 100:F0}cm pullAt={pullAt:F2}s clawToBall={clawToBall * 100:F1}cm fromFaceCentre ball={ballFromFace * 100:F1} claw={clawFromFace * 100:F1} robot={robotFromFace * 100:F1}cm wheelGapAtPull={ballGap:F1}cm robotSpeedAtPull={robotSpeedAtPull:F2} ballPeak={ballPeak:F2}m/s approach(t:speed@dist)=[{string.Join(" ", phases)}] stopAt={endDist:F3}m side={endSide * 100:F1}cm yawErr={faceYaw:F1}deg speed={speed:F2} seated={seated.Length}/{scored.Count} knocked={fell} nudged={nudged} worstImpulse={worst:F1} [{string.Join(" ", moved)}]");
            yield return DropHeld();
            yield return Tap(Key.Semicolon);
            yield return Tap(Key.Z);
            var spot = 0;
            foreach (var x in scored)
                if (x.piece) Put(x.piece, new Vector3(-3.4f, 0.1f, 1.2f + 0.45f * spot++), Quaternion.identity);
            yield return new WaitForSeconds(0.3f);
        }
    }

    // How long each part of the climb takes with the real keys: first press (deploy), second press (pull, no cage load).
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator ClimbTiming()
    {
        yield return Boot();
        var climber = Field("climber") as GenericJoint;
        var arm = Field("arm") as GenericJoint;
        var wrist = Field("wrist") as GenericJoint;
        var elev = Field("elevator") as GenericElevator;
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        yield return new WaitForSeconds(1f);
        foreach (var phase in new[] { "deploy", "pull" })
        {
            var series = new System.Collections.Generic.List<string>();
            yield return Tap(Key.C); // includes 0.3 s of the press itself
            var last = (c: climber.GetSingleAxisAngle(JointAxis.Y), a: arm.GetSingleAxisAngle(JointAxis.X), w: wrist.GetSingleAxisAngle(JointAxis.X), h: elev.GetElevatorHeight());
            var settled = 0f;
            for (var e = 0f; e < 5f; e += 0.1f)
            {
                yield return new WaitForSeconds(0.1f);
                var now = (c: climber.GetSingleAxisAngle(JointAxis.Y), a: arm.GetSingleAxisAngle(JointAxis.X), w: wrist.GetSingleAxisAngle(JointAxis.X), h: elev.GetElevatorHeight());
                series.Add($"{e + 0.1f:F1}:c{now.c:F0}/a{now.a:F0}/w{now.w:F0}/h{now.h:F1}");
                if (Mathf.Abs(Mathf.DeltaAngle(now.c, last.c)) > 0.3f || Mathf.Abs(Mathf.DeltaAngle(now.a, last.a)) > 0.3f || Mathf.Abs(Mathf.DeltaAngle(now.w, last.w)) > 0.3f || Mathf.Abs(now.h - last.h) > 0.05f) settled = e + 0.1f;
                last = now;
            }
            L($"RESULT climbTiming {phase} sp={_robot.CurrentSetpoint} settledAfterPress={settled + 0.3f:F1}s (+0.3 press) [{string.Join(" ", series.Take(40))}]");
        }
    }

    // The algae intake's geometry and what the ball does on the way in: wheels, plates, detection box and states, seen from
    // the intake's target, then the ball's gap to the wheels step by step.
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator AlgaeProbe()
    {
        yield return Boot();
        var reef = ReefCentre();
        var t = _robot.transform;
        var ai = Field("algaeIntake") as ReefscapeGamePieceIntake;
        var target = (Transform)typeof(ReefscapeGamePieceIntake).BaseType.GetField("target", F).GetValue(ai);
        var rollers = (Field("clawRollers") as Component[]);
        var stow = Field("algaeStowState") as RobotFramework.GamePieceSystem.GamePieceState;
        yield return DropHeld();
        L($"probe target={target.name} robotLocal={t.InverseTransformPoint(target.position):F3} stowState={stow?.name} speed={stow?.stateMoveSpeed} smooth={stow?.smoothHandoff} ang={stow?.angularSpeed} target={stow?.stateTarget?.name} stowLocal={(stow != null && stow.stateTarget ? t.InverseTransformPoint(stow.stateTarget.position) : Vector3.zero):F3}");
        foreach (var d in new[] { 90f })
        {
            yield return Tap(Key.Semicolon);
            var face = GameObject.FindGameObjectsWithTag("ReefFace").Where(f => Vector3.ProjectOnPlane(f.transform.position - reef, Vector3.up).magnitude < 2f).ToArray();
            var algaeAll = Object.FindObjectsByType<PieceCtrl>(FindObjectsSortMode.None).Where(p => p.PieceName == "Algae" && p.transform.position.y > 0.4f && Vector3.ProjectOnPlane(p.transform.position - reef, Vector3.up).magnitude < 1.6f).ToArray();
            foreach (var (name, key, facing) in new[] { ("low", Key.I, true), ("high", Key.O, false) })
            {
                var midY = (algaeAll.Min(p => p.transform.position.y) + algaeAll.Max(p => p.transform.position.y)) / 2;
                var algae = algaeAll.First(p => p.gameObject.activeInHierarchy && p.transform.position.y < midY == (name == "low"));
                var f = face.OrderBy(x => Vector3.ProjectOnPlane(x.transform.position - algae.transform.position, Vector3.up).sqrMagnitude).First().transform;
                var dir = Vector3.ProjectOnPlane(f.position - reef, Vector3.up).normalized;
                yield return Tap(key);
                PlaceRobot(reef + dir * 1.9f, facing ? -dir : dir);
                yield return new WaitForSeconds(2.2f);
                L($"probe {name} ball colliders=[{string.Join("; ", algae.GetComponentsInChildren<Collider>().Select(c => $"{c.name}:{c.GetType().Name}:{c.bounds.size.x:F3}:tag={c.tag}:layer={LayerMask.LayerToName(c.gameObject.layer)}"))}] mass={algae.GetComponent<Rigidbody>()?.mass} kinematic={algae.GetComponent<Rigidbody>()?.isKinematic}");
                foreach (var b in Boxes(ai)) L($"probe {name} box {b.name} targetLocal={target.InverseTransformPoint(b.transform.position):F3} sizeWorld={Vector3.Scale(b.size, b.transform.lossyScale):F3} robotLocal={t.InverseTransformPoint(b.transform.position):F3}");
                foreach (var w in rollers) L($"probe {name} roller {w.name} targetLocal={target.InverseTransformPoint(w.transform.position):F3} robotLocal={t.InverseTransformPoint(w.transform.position):F3} colliders=[{string.Join(",", w.GetComponentsInChildren<Collider>().Select(c => c.name + ":" + c.GetType().Name + ":" + c.bounds.size.ToString("F3")))}] renderSize={(w.GetComponentInChildren<Renderer>() ? w.GetComponentInChildren<Renderer>().bounds.size : Vector3.zero):F3}");
                foreach (var c in (Field("wrist") as Component).GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).OrderBy(c => c.name))
                    L($"probe {name} wristCollider {c.transform.parent?.name}/{c.name} {c.GetType().Name} centreTargetLocal={target.InverseTransformPoint(c.bounds.center):F3} worldSize={c.bounds.size:F3}");
                L($"probe {name} ball targetLocal={target.InverseTransformPoint(algae.transform.position):F3} robotToReef={Vector3.Dot(t.position - reef, dir):F3}");
                Keys(Key.LeftShift, Key.Q);
                var clog = algae.gameObject.AddComponent<ContactLog>();
                var seen = "";
                var trace = new System.Collections.Generic.List<string>();
                var heldAt = -1f;
                for (var e = 0f; e < 5f; e += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    var rb = algae.GetComponent<Rigidbody>();
                    var gap = rollers.Min(w => (w.transform.position - algae.transform.position).magnitude);
                    if (trace.Count < 60 && ai.requestIntake && Mathf.RoundToInt(e / Time.fixedDeltaTime) % 10 == 0)
                        trace.Add($"{e:F2}:toTarget={(algae.transform.position - target.position).magnitude * 100:F0}cm toWheel={gap * 100:F0}cm v={(rb ? rb.velocity.magnitude : 0):F2} req={ai.requestIntake} has={ai.hasGamePiece} sec={ai.securedGamePiece} robot={Vector3.Dot(t.position - reef, dir):F2}");
                    if (heldAt < 0 && NodeOf("Algae").HasPiece()) { heldAt = e; seen = string.Join("; ", clog.hits.Select(kv => kv.Key)); }
                    if (heldAt > 0 && e > heldAt + 1.5f) break;
                }
                if (seen == "") seen = string.Join("; ", clog.hits.OrderByDescending(kv => kv.Value.n).Take(10).Select(kv => $"{kv.Key} n={kv.Value.n} imp={kv.Value.impulse:F1}"));
                L($"probe {name} {(facing ? "front" : "back")} heldAt={heldAt:F2} contacts=[{seen}] trace {string.Join(" | ", trace)}");
                Object.Destroy(clog);
                Keys(Key.LeftShift);
                L($"probe {name} held ball targetLocal={target.InverseTransformPoint(algae.transform.position):F3} robotLocal={t.InverseTransformPoint(algae.transform.position):F3}");
                yield return DropHeld();
                yield return Tap(Key.Z);
            }
        }
    }

    // Overlap depth of two meshes' oriented bounding boxes (separating axis test); negative when apart.
    static float ObbDepth(MeshFilter a, MeshFilter b, out Vector3 centre)
    {
        Vector3[] Half(MeshFilter m) => new[] { m.transform.TransformVector(Vector3.right * m.sharedMesh.bounds.extents.x), m.transform.TransformVector(Vector3.up * m.sharedMesh.bounds.extents.y), m.transform.TransformVector(Vector3.forward * m.sharedMesh.bounds.extents.z) };
        var ca = a.transform.TransformPoint(a.sharedMesh.bounds.center);
        var cb = b.transform.TransformPoint(b.sharedMesh.bounds.center);
        centre = (ca + cb) / 2;
        var ha = Half(a); var hb = Half(b);
        var axes = new System.Collections.Generic.List<Vector3>();
        foreach (var x in ha) axes.Add(x);
        foreach (var x in hb) axes.Add(x);
        foreach (var x in ha) foreach (var y in hb) axes.Add(Vector3.Cross(x, y));
        var depth = float.MaxValue;
        foreach (var axis in axes)
        {
            if (axis.sqrMagnitude < 1e-12f) continue;
            var l = axis.normalized;
            var r = ha.Sum(x => Mathf.Abs(Vector3.Dot(x, l))) + hb.Sum(x => Mathf.Abs(Vector3.Dot(x, l)));
            depth = Mathf.Min(depth, r - Mathf.Abs(Vector3.Dot(cb - ca, l)));
            if (depth < 0) return depth;
        }
        return depth;
    }

    (MeshFilter[] mine, MeshFilter[] others) IntakeAndElevatorMeshes()
    {
        var intake = (Field("intake") as Component).transform;
        var armT = (Field("arm") as Component).transform;
        return (intake.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh && m.GetComponent<Renderer>().enabled).ToArray(),
            _robot.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh && !m.transform.IsChildOf(intake) && !m.transform.IsChildOf(armT) && !m.GetComponentInParent<PieceCtrl>()
                && (m.name.Contains("V Tube") || m.transform.GetComponentsInParent<Transform>().Any(x => x.name is "Elevator" or "Stage One" or "Stage Two"))).ToArray());
    }

    // Does the ground intake's real mesh pass through the elevator or its braces? Every step of the swing in: intake
    // vertices inside each elevator-side part's box, with how deep the deepest one is.
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator IntakeMeshOverlap()
    {
        yield return Boot();
        var t = _robot.transform;
        yield return DropHeld();
        PlaceRobot(new Vector3(-2.2f, 0, 2.2f), Vector3.forward);
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        var (mine, others) = IntakeAndElevatorMeshes();
        L($"meshOverlap intakeMeshes={mine.Length} elevatorParts={others.Length}");
        Keys(Key.LeftShift);
        yield return new WaitForSeconds(2f);
        Keys(Key.Z);
        var worst = new System.Collections.Generic.Dictionary<string, (float depth, float ang, Vector3 at, Vector3 sizeA, Vector3 sizeB)>();
        var byAngle = new System.Collections.Generic.List<string>();
        var lastAng = 999f;
        var lowest = 999f;
        int inAt = -1, settledAt = -1;
        for (var k = 0; k < 600; k++)
        {
            yield return new WaitForFixedUpdate();
            if (k == 40) Keys();
            var ang = Mathf.DeltaAngle(0, (Field("intake") as GenericJoint).GetSingleAxisAngle(JointAxis.Y) + 71.4f);
            lowest = Mathf.Min(lowest, ang);
            if (ang < 80f && inAt < 0) inAt = k;
            if (ang < 10f && settledAt < 0) settledAt = k;
            if (Mathf.Abs(ang - lastAng) < 2f) continue;
            lastAng = ang;
            var deepest = 0f;
            var who = "";
            foreach (var m in mine)
            {
                var mb = m.GetComponent<Renderer>().bounds;
                foreach (var o in others)
                {
                    if (!mb.Intersects(o.GetComponent<Renderer>().bounds)) continue;
                    var depth = ObbDepth(m, o, out var at);
                    if (depth <= 0.002f) continue;
                    var key = $"{m.name}>{o.name}";
                    if (!worst.TryGetValue(key, out var w) || depth > w.depth)
                        worst[key] = (depth, ang, t.InverseTransformPoint(at), Vector3.Scale(m.sharedMesh.bounds.size, m.transform.lossyScale), Vector3.Scale(o.sharedMesh.bounds.size, o.transform.lossyScale));
                    if (depth > deepest) { deepest = depth; who = key; }
                }
            }
            byAngle.Add($"{ang:F0}deg:{deepest * 100:F1}cm({who})");
        }
        L($"RESULT meshOverlap settled={(Field("intake") as GenericJoint).GetSingleAxisAngle(JointAxis.Y) + 71.4f - 360f:F1}deg lowest={lowest:F1}deg swingTook={(settledAt - inAt) * Time.fixedDeltaTime:F2}s byAngle=[{string.Join(" ", byAngle)}]");
        foreach (var kv in worst.OrderByDescending(kv => kv.Value.depth).Take(25))
            L($"RESULT meshOverlap {kv.Key} deepest={kv.Value.depth * 100:F1}cm at {kv.Value.ang:F1}deg robotLocal={kv.Value.at:F3} sizes={kv.Value.sizeA:F3}/{kv.Value.sizeB:F3}");
    }

    // The coral station, repeatedly, each side: caught or not, how long, and if it wedged, on what.
    [UnityTest]
    [Timeout(900000)]
    public IEnumerator HpRepeat()
    {
        yield return Boot();
        var t = _robot.transform;
        yield return DropHeld();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        var stations = Object.FindObjectsByType<Games.Reefscape.FieldScripts.CoralStation>(FindObjectsSortMode.None).Where(s => s.Alliance == _robot.Alliance).ToArray();
        var spawn = (Transform)typeof(Games.Reefscape.FieldScripts.CoralStation).GetField("coralSpawnpoint", F).GetValue(stations[0]);
        var hpi = Field("hpCoralIntake") as ReefscapeGamePieceIntake;
        var bodies = _robot.GetComponentsInChildren<Rigidbody>().Select(b => $"({b.name})").ToArray();
        var sf = spawn.forward; sf.y = 0; sf.Normalize();
        foreach (var facing in new[] { 1f, -1f })
        for (var rep = 0; rep < 10; rep++)
        {
            PlaceRobot(spawn.position + sf * 0.75f, -sf * facing);
            yield return new WaitForSeconds(1f);
            Keys(Key.LeftShift);
            var start = Time.time;
            ContactLog log = null;
            PieceCtrl last = null;
            var box = Boxes(hpi)[0].transform;
            while (Time.time - start < 7f && !NodeOf("Coral").HasPiece())
            {
                var c = FreePiece("Coral", box.position);
                if (c != last) { if (log) Object.Destroy(log); log = c ? c.gameObject.AddComponent<ContactLog>() : null; last = c; }
                _robot.GetComponent<Rigidbody>().AddForce(-sf * 300f);
                yield return new WaitForFixedUpdate();
            }
            var took = Time.time - start;
            var held = NodeOf("Coral").HasPiece();
            var where = last ? box.InverseTransformPoint(last.transform.position).ToString("F2") : "-";
            var state = $"has={hpi.hasGamePiece} sec={hpi.securedGamePiece}";
            var hits = log ? string.Join("; ", log.hits.OrderByDescending(kv => kv.Value.n).Take(4).Select(kv => $"{kv.Key} n={kv.Value.n}")) : "";
            if (log) Object.Destroy(log);
            Keys();
            yield return new WaitForSeconds(0.5f);
            L($"RESULT hpRepeat facing={facing} rep={rep} held={held} took={took:F1}s {state} coral(box)={where} {(held ? "" : $"touching=[{hits}]")}");
            yield return DropHeld();
        }
    }

    // Press intake, let go while the arm is still moving: every joint's path after the release, and whether it ends up back
    // at stow. Reversals of direction, how far past its target it went, what it was asked to do, and how long until settled.
    [UnityTest]
    [Timeout(900000)]
    public IEnumerator IntakeRelease()
    {
        yield return Boot();
        var t = _robot.transform;
        var arm = Field("arm") as GenericJoint;
        var wrist = Field("wrist") as GenericJoint;
        var intake = Field("intake") as GenericJoint;
        var elev = Field("elevator") as GenericElevator;
        var rb = _robot.GetComponent<Rigidbody>();
        yield return DropHeld();
        foreach (var (label, ground, algae) in new[] { ("ground", true, false), ("station", false, false), ("algae", false, true) })
        {
            if (algae && _robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Algae) yield return Tap(Key.Semicolon);
            if (!algae && _robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Coral) yield return Tap(Key.Semicolon);
            if (!algae && (_robot.CurrentCoralStationMode.DropType == DropType.Ground) != ground) yield return Tap(Key.V);
            // (hold, then a gap before pressing again and holding for a second; gap 0 = no second press)
            foreach (var (hold, gap) in ground
                         ? new[] { (0.1f, 0f), (0.2f, 0f), (0.25f, 0f), (0.3f, 0f), (0.35f, 0f), (0.4f, 0f), (0.45f, 0f), (0.5f, 0f), (0.55f, 0f), (0.7f, 0f), (1f, 0f), (0.3f, 0.1f), (0.3f, 0.2f), (0.3f, 0.4f), (0.4f, 0.1f), (0.4f, 0.3f), (1f, 0.2f), (1f, 0.5f) }
                         : new[] { (0.1f, 0f), (0.2f, 0f), (0.3f, 0f), (0.5f, 0f), (0.2f, 0.1f) })
            {
                PlaceRobot(new Vector3(-2.2f, 0, 2.2f), Vector3.forward);
                yield return Tap(Key.Z);
                yield return new WaitForSeconds(1.5f);
                Keys(Key.LeftShift);
                var armMax = 0f; var wristMax = 0f;
                var a0 = arm.GetSingleAxisAngle(JointAxis.X); var w0 = wrist.GetSingleAxisAngle(JointAxis.X);
                for (var e = 0f; e < hold; e += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    armMax = Mathf.Max(armMax, Mathf.Abs(Mathf.DeltaAngle(a0, arm.GetSingleAxisAngle(JointAxis.X))));
                }
                var atRelease = $"arm{Mathf.DeltaAngle(a0, arm.GetSingleAxisAngle(JointAxis.X)):F0}/wrist{Mathf.DeltaAngle(w0, wrist.GetSingleAxisAngle(JointAxis.X)):F0}/intake{Mathf.DeltaAngle(291.5f, intake.GetSingleAxisAngle(JointAxis.Y)):F0}/elev{elev.GetElevatorHeight():F1}";
                Keys();
                var again = gap > 0;
                var pressAt = gap;
                var releaseAt = gap + 1f;
                float lastA = arm.GetSingleAxisAngle(JointAxis.X), lastW = wrist.GetSingleAxisAngle(JointAxis.X), lastI = intake.GetSingleAxisAngle(JointAxis.Y);
                int aDir = 0, wDir = 0, iDir = 0, aRev = 0, wRev = 0, iRev = 0;
                var aPeak = 0f; var wPeak = 0f; var settled = 0f;
                var aFar = 0f; var wFar = 0f;
                var lift = 0f; var tilt = 0f; var spin = 0f;
                var spTrace = new System.Collections.Generic.List<string>();
                var lastSp = "";
                var y0 = t.position.y;
                for (var e = 0f; e < (again ? 5f : 4f); e += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    if (again && e >= pressAt && e < pressAt + Time.fixedDeltaTime * 1.5f) Keys(Key.LeftShift);
                    if (again && e >= releaseAt && e < releaseAt + Time.fixedDeltaTime * 1.5f) Keys();
                    float a = arm.GetSingleAxisAngle(JointAxis.X), w = wrist.GetSingleAxisAngle(JointAxis.X), i = intake.GetSingleAxisAngle(JointAxis.Y);
                    float da = Mathf.DeltaAngle(lastA, a), dw = Mathf.DeltaAngle(lastW, w), di = Mathf.DeltaAngle(lastI, i);
                    void Rev(float d, ref int dir, ref int rev) { if (Mathf.Abs(d) < 0.15f) return; var sg = d > 0 ? 1 : -1; if (dir != 0 && sg != dir) rev++; dir = sg; }
                    Rev(da, ref aDir, ref aRev); Rev(dw, ref wDir, ref wRev); Rev(di, ref iDir, ref iRev);
                    aPeak = Mathf.Max(aPeak, Mathf.Abs(da) / Time.fixedDeltaTime); wPeak = Mathf.Max(wPeak, Mathf.Abs(dw) / Time.fixedDeltaTime);
                    aFar = Mathf.Max(aFar, Mathf.Abs(Mathf.DeltaAngle(a0, a))); wFar = Mathf.Max(wFar, Mathf.Abs(Mathf.DeltaAngle(w0, w)));
                    if (Mathf.Abs(da) > 0.02f || Mathf.Abs(dw) > 0.02f || Mathf.Abs(di) > 0.02f) settled = e;
                    lift = Mathf.Max(lift, t.position.y - y0); tilt = Mathf.Max(tilt, Vector3.Angle(t.up, Vector3.up)); spin = Mathf.Max(spin, rb.angularVelocity.magnitude);
                    lastA = a; lastW = w; lastI = i;
                    var sp = _robot.CurrentSetpoint.ToString();
                    if (sp != lastSp && spTrace.Count < 8) { spTrace.Add($"{e:F2}:{sp}"); lastSp = sp; }
                }
                var offA = Mathf.DeltaAngle(a0, arm.GetSingleAxisAngle(JointAxis.X)); var offW = Mathf.DeltaAngle(w0, wrist.GetSingleAxisAngle(JointAxis.X));
                var cmds = string.Join(" ", new[] { "_armTargetAngle", "_wristTargetAngle", "_intakeTargetAngle", "_elevatorTargetHeight", "_isRetractingFromGround" }.Select(f => $"{f.TrimStart('_').Replace("TargetAngle", "").Replace("TargetHeight", "")}={Field(f)}"));
                L($"RESULT intakeRelease {label} hold={hold:F2}s gap={gap:F1}s atRelease={atRelease} reversals arm={aRev} wrist={wRev} intake={iRev} furthest arm={aFar:F0} wrist={wFar:F0} peak arm={aPeak:F0} wrist={wPeak:F0}deg/s settled={settled:F2}s endOff arm={offA:F1} wrist={offW:F1} lift={lift * 100:F1}cm tilt={tilt:F1} spin={spin:F2} sp=[{string.Join(" ", spTrace)}] end[{cmds}]");
            }
        }
    }

    // Tapping intake over and over at varied rhythms, as a driver mashing it would: the worst the robot does, per rhythm.
    [UnityTest]
    [Timeout(600000)]
    public IEnumerator IntakeSpam()
    {
        yield return Boot();
        var t = _robot.transform;
        var arm = Field("arm") as GenericJoint;
        var intake = Field("intake") as GenericJoint;
        yield return DropHeld();
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Coral) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        var rng = new System.Random(971);
        foreach (var (label, holdMin, holdMax, gapMin, gapMax) in new[]
                 {
                     ("fast", 0.1f, 0.3f, 0.1f, 0.3f), ("medium", 0.2f, 0.6f, 0.2f, 0.6f), ("slow", 0.3f, 0.9f, 0.4f, 1.2f), ("mixed", 0.05f, 1f, 0.05f, 1f),
                 })
        {
            PlaceRobot(new Vector3(-2.2f, 0, 2.2f), Vector3.forward);
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1.5f);
            var a0 = arm.GetSingleAxisAngle(JointAxis.X);
            var p0 = t.position;
            var y0 = t.position.y;
            float lift = 0, tilt = 0, far = 0, aPeak = 0, drift = 0;
            var lastA = a0;
            var airborne = 0f;
            var worstAt = 0f;
            var clock = 0f;
            for (var c = 0; c < 16; c++)
            {
                var hold = Mathf.Lerp(holdMin, holdMax, (float)rng.NextDouble());
                var gap = Mathf.Lerp(gapMin, gapMax, (float)rng.NextDouble());
                for (var phase = 0; phase < 2; phase++)
                {
                    Keys(phase == 0 ? new[] { Key.LeftShift } : Array.Empty<Key>());
                    for (var e = 0f; e < (phase == 0 ? hold : gap); e += Time.fixedDeltaTime)
                    {
                        yield return new WaitForFixedUpdate();
                        clock += Time.fixedDeltaTime;
                        var a = arm.GetSingleAxisAngle(JointAxis.X);
                        far = Mathf.Max(far, Mathf.Abs(Mathf.DeltaAngle(a0, a)));
                        aPeak = Mathf.Max(aPeak, Mathf.Abs(Mathf.DeltaAngle(lastA, a)) / Time.fixedDeltaTime);
                        lastA = a;
                        var up = t.position.y - y0;
                        if (up > lift) { lift = up; worstAt = clock; }
                        if (up > 0.02f) airborne += Time.fixedDeltaTime;
                        tilt = Mathf.Max(tilt, Vector3.Angle(t.up, Vector3.up));
                        drift = Mathf.Max(drift, Vector3.ProjectOnPlane(t.position - p0, Vector3.up).magnitude);
                    }
                }
            }
            Keys();
            yield return new WaitForSeconds(2f);
            L($"RESULT intakeSpam {label} over {clock:F0}s: lift={lift * 100:F1}cm at {worstAt:F1}s airborne={airborne:F2}s tilt={tilt:F1}deg armFar={far:F0} (hard stop 165) armPeak={aPeak:F0}deg/s drift={drift * 100:F0}cm endsAtStow arm={Mathf.DeltaAngle(a0, arm.GetSingleAxisAngle(JointAxis.X)):F1} setpoint={_robot.CurrentSetpoint}");
        }
    }

    // What touches the arm and wrist bodies during a test.
    public class ContactLog : MonoBehaviour
    {
        public readonly System.Collections.Generic.Dictionary<string, (int n, float impulse)> hits = new();
        void OnCollisionStay(Collision c) => Note(c);
        void OnCollisionEnter(Collision c) => Note(c);
        void Note(Collision c)
        {
            if (c.contactCount == 0) return;
            var key = $"{c.GetContact(0).thisCollider.name} x {c.collider.name} ({c.collider.attachedRigidbody?.name})";
            hits.TryGetValue(key, out var v);
            hits[key] = (v.n + 1, Mathf.Max(v.impulse, c.impulse.magnitude));
        }
    }

    // ---- holding both align buttons: the robot settles on the middle of the nearest reef face, facing it and away
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator AlignBoth()
    {
        yield return Boot();
        var reef = GameObject.Find(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? "BlueReef" : "RedReef").transform.position;
        reef.y = 0;
        var t = _robot.transform;
        foreach (var facing in new[] { true, false })
        {
            var ang = Mathf.Deg2Rad * (facing ? 30f : 150f);
            var d = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            PlaceRobot(reef + d * 1.9f + Vector3.Cross(Vector3.up, d) * 0.3f, facing ? -d : d);
            yield return new WaitForSeconds(0.5f);
            var start = t.position;
            Keys(Key.Q, Key.E);
            var far = 0f;
            for (var e = 0f; e < 3f; e += Time.fixedDeltaTime)
            {
                yield return new WaitForFixedUpdate();
                far = Mathf.Max(far, (t.position - start).magnitude);
            }
            Keys();
            var face = GameObject.FindGameObjectsWithTag("ReefFace").Select(g => g.GetComponent<Games.Reefscape.FieldScripts.AlignNode>())
                .Where(n => n).OrderBy(n => (n.transform.position - t.position).sqrMagnitude).First();
            var mid = (face.LeftNode.transform.position + face.RightNode.transform.position) / 2;
            var lateral = Vector3.Dot(t.position - mid, face.LeftNode.transform.right);
            var aa = _robot.GetComponent<ReefscapeAutoAlign>();
            var reefCols = Physics.OverlapSphere(t.position, 1.5f).Where(c => !c.isTrigger && !c.transform.IsChildOf(t) && c.transform.root.name.Contains("Reef")).ToArray();
            var gap = reefCols.Length == 0 ? -1f : _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled && !(c is MeshCollider m && !m.convex) && !(c is WheelCollider))
                .SelectMany(rc => reefCols.Where(c => !(c is MeshCollider m2 && !m2.convex)).Select(rf => { var p = rf.ClosestPoint(rc.bounds.center); return Vector3.Distance(rc.ClosestPoint(p), p); })).DefaultIfEmpty(-1).Min();
            L($"RESULT alignBoth facing={facing} lateralFromFaceCentre={lateral:F3}m movedMax={far:F2}m distToReef={(t.position - reef).magnitude:F2}m robotToReefGap={gap * 100:F1}cm alignEnabledAfter={aa.enabled}");
            yield return new WaitForSeconds(0.5f);
        }
    }

    [UnityTest]
    [Timeout(900000)]
    public IEnumerator Scoring()
    {
        yield return Boot();
        var reef = GameObject.Find(_robot.Alliance == MoSimCore.Enums.Alliance.Blue ? "BlueReef" : "RedReef").transform.position;
        reef.y = 0;
        L($"reef {reef:F2}");
        var trials = new[]
        {
            (Key.U, true), (Key.I, true), (Key.O, true), (Key.P, true),
            (Key.I, false), (Key.O, false), (Key.P, false), (Key.U, false),
        };
        for (var r = 0; r < 2 * trials.Length; r++)
        {
            var n = r % trials.Length;
            var rep = r / trials.Length;
            var (level, facing) = trials[n];
            var name = level switch { Key.U => "L1", Key.I => "L2", Key.O => "L3", _ => "L4" };
            var only = Environment.GetEnvironmentVariable("TEST_LEVELS"); // e.g. "L4" or "L2,L3"
            if (!string.IsNullOrEmpty(only) && !only.Split(',').Contains(name)) continue;
            yield return LoadCoral();
            // A different reef face (and pole side) for every trial so earlier coral never fills the branch.
            var ang = Mathf.Deg2Rad * (60f * ((n + 3 * rep) % 6) + 30f);
            var d = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            PlaceRobot(reef + d * 1.75f, facing ? -d : d);
            yield return new WaitForSeconds(0.5f);
            var align = rep == 0 ? Key.Q : Key.E; // other pole of the face on the second pass
            var raiseFirst = Environment.GetEnvironmentVariable("TEST_RAISE_FIRST") == "1"; // like a driver: level, then align
            if (raiseFirst)
            {
                yield return Tap(level);
                yield return new WaitForSeconds(2f);
                Keys(align);
                // Every frame of the approach: robot parts pressing on anything that isn't a game piece or the robot.
                var mine = _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled).ToArray();
                var pieceLayers = LayerMask.GetMask("Coral", "Algae");
                var worst = new System.Collections.Generic.Dictionary<string, float>();
                for (var e = 0f; e < 3f; e += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    foreach (var part in mine)
                    foreach (var o in Physics.OverlapBox(part.bounds.center, part.bounds.extents + Vector3.one * 0.005f))
                    {
                        if (o.isTrigger || o.transform.IsChildOf(_robot.transform) || ((1 << o.gameObject.layer) & pieceLayers) != 0) continue;
                        if (!Physics.ComputePenetration(part, part.transform.position, part.transform.rotation, o, o.transform.position, o.transform.rotation, out _, out var dist)) continue;
                        var key = $"{part.transform.parent?.name}/{part.name}>{o.transform.parent?.name}/{o.name}";
                        worst[key] = Mathf.Max(worst.TryGetValue(key, out var w) ? w : 0, dist);
                    }
                }
                var aa = _robot.GetComponent<ReefscapeAutoAlign>();
                L($"alignAfterRaise {name} {(facing ? "front" : "back")} residual={(aa ? aa.getDistance() : -1):F3}m " +
                  $"pressing=[{string.Join(", ", worst.OrderByDescending(x => x.Value).Take(6).Select(x => $"{x.Key}:{x.Value * 100:F1}cm"))}]");
            }
            else
            {
                Keys(align);
                yield return new WaitForSeconds(2f);
            }
            if (!raiseFirst) yield return Tap(level);
            {
                // Mechanism speeds on the way up: peak elevator speed and peak arm / wrist angular speed.
                var elev = Field("elevator") as GenericElevator;
                var armT = (Field("arm") as Component)?.transform;
                var wristT = (Field("wrist") as Component)?.transform;
                float h0 = elev ? elev.GetElevatorHeight() : 0, hPeak = 0, aPeak = 0, wPeak = 0;
                var a0 = armT ? armT.localRotation : Quaternion.identity;
                var w0 = wristT ? wristT.localRotation : Quaternion.identity;
                var settle = 0f;
                // Wobble: how far the claw still wanders (peak to peak, robot frame) and the fastest arm / wrist motion
                // in the last second at the setpoint.
                var clawT = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
                Vector3 cMin = Vector3.positiveInfinity, cMax = Vector3.negativeInfinity;
                float aLate = 0, wLate = 0;
                var heights = new System.Collections.Generic.List<float>();
                for (var e = 0f; e < 3f; e += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    var h = elev ? elev.GetElevatorHeight() : 0;
                    heights.Add(h);
                    var dh = Mathf.Abs(h - h0) / Time.fixedDeltaTime; h0 = h;
                    var da = armT ? Quaternion.Angle(a0, armT.localRotation) / Time.fixedDeltaTime : 0; if (armT) a0 = armT.localRotation;
                    var dw = wristT ? Quaternion.Angle(w0, wristT.localRotation) / Time.fixedDeltaTime : 0; if (wristT) w0 = wristT.localRotation;
                    hPeak = Mathf.Max(hPeak, dh); aPeak = Mathf.Max(aPeak, da); wPeak = Mathf.Max(wPeak, dw);
                    if (dh > 0.05f || da > 5f || dw > 5f) settle = e;
                    if (e >= 2f && clawT)
                    {
                        var c = _robot.transform.InverseTransformPoint(clawT.position);
                        cMin = Vector3.Min(cMin, c); cMax = Vector3.Max(cMax, c);
                        aLate = Mathf.Max(aLate, da); wLate = Mathf.Max(wLate, dw);
                    }
                }
                var reach = heights.FindIndex(x => Mathf.Abs(x - heights[^1]) < 1f); // within 1 in of where it ends
                L($"rise {name} {(facing ? "front" : "back")} elevator {heights[0]:F1}->{heights[^1]:F1}in reached in {reach * Time.fixedDeltaTime:F2}s");
                L($"wobble {name} {(facing ? "front" : "back")} claw p-p={(cMax - cMin) * 100:F2}cm armRateMax={aLate:F1}deg/s wristRateMax={wLate:F1}deg/s");
                L($"speeds {name} {(facing ? "front" : "back")} elevPeak={hPeak:F2}(units/s) armPeak={aPeak:F0}deg/s wristPeak={wPeak:F0}deg/s settledAfter={settle:F2}s elevHeight={(elev ? elev.GetElevatorHeight() : 0):F2}");
            }
            L($"{name} {(facing ? "front" : "back")} at setpoint: {Status()}");
            L($"geom {name} {(facing ? "front" : "back")} {ScoreGeometry(name)}");
            {
                // How far the robot still is from its align target, and what of the robot is pressing on the field.
                var aa = _robot.GetComponent<ReefscapeAutoAlign>();
                var mine = _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled).ToArray();
                var touching = mine.SelectMany(r => Physics.OverlapBox(r.bounds.center, r.bounds.extents + Vector3.one * 0.005f)
                        .Where(o => !o.isTrigger && !o.transform.IsChildOf(_robot.transform) && (o.attachedRigidbody == null || o.attachedRigidbody.isKinematic) &&
                                    Physics.ComputePenetration(r, r.transform.position, r.transform.rotation, o, o.transform.position, o.transform.rotation, out _, out var dist) && dist > 0.002f)
                        .Select(o => $"{r.name}>{o.name}"))
                    .Distinct().Take(8);
                // Clearance toward the reef: how far the whole robot could still move straight at it.
                var toReef = Vector3.ProjectOnPlane(reef - _robot.transform.position, Vector3.up).normalized;
                var clear = _robot.GetComponent<Rigidbody>().SweepTest(toReef, out var reefHit, 0.3f, QueryTriggerInteraction.Ignore) ? $"{reefHit.distance * 100:F1}cm({reefHit.collider.name})" : ">30cm";
                L($"align {name} {(facing ? "front" : "back")} residual={(aa ? aa.getDistance() : -1):F3}m offsetZ={(aa ? aa.offset.z : 0):F1}in offsetX={(aa ? aa.offset.x : 0):F1}in clearToReef={clear} pressing=[{string.Join(",", touching)}]");
            }
            L($"overlap {name} {(facing ? "front" : "back")} coral vs robot: {Overlaps(NodeOf("Coral").controller)}");
            var before = Score();
            var coral = NodeOf("Coral").controller;
            Keys(_held.Append(Key.K).ToArray());
            // Coral motion right after release (robot-local): position, speed, spin.
            var crb = coral ? coral.GetComponent<Rigidbody>() : null;
            for (var k = 0; k < (name == "L4" ? 120 : 30) && crb; k++)
            {
                yield return new WaitForFixedUpdate();
                if (k == 6) Keys(_held.Where(x => x != Key.K).ToArray());
                if (k < 6)
                {
                    var own = PieceColliders(coral);
                    var near = Physics.OverlapSphere(coral.transform.position, 0.2f, ~0, QueryTriggerInteraction.Ignore)
                        .Where(c => !own.Contains(c))
                        .Select(c => $"{(c.transform.IsChildOf(_robot.transform) ? "robot" : c.transform.root.name)}:{c.transform.parent?.name}/{c.name}" +
                                     $"@{(c.ClosestPoint(coral.transform.position) - coral.transform.position).magnitude * 1000:F0}mm");
                    L($"near {name} {(facing ? "front" : "back")} k={k} vel={_robot.transform.InverseTransformDirection(crb.velocity):F2} [{string.Join(", ", near)}]");
                }
                if (k % 3 == 0)
                    L($"release {name} {(facing ? "front" : "back")} k={k} pos={_robot.transform.InverseTransformPoint(coral.transform.position):F2} " +
                      $"vel={_robot.transform.InverseTransformDirection(crb.velocity):F2} spin={crb.angularVelocity.magnitude:F1}rad/s touching=[{Overlaps(coral)}]");
            }
            yield return new WaitForSeconds(2f);
            var after = Score();
            Keys();
            L($"RESULT {name} {(facing ? "facingReef" : "facingAway")} rep={rep} released={!NodeOf("Coral").HasPiece()} " +
              $"coralScored+={after.coral - before.coral} coralPoints+={after.pts - before.pts} " +
              $"coral(robot-local)={(coral ? _robot.transform.InverseTransformPoint(coral.transform.position).ToString("F2") : "-")}");
            if (name == "L4" && coral)
            {
                // How the coral came to rest on the L4 branch: its axis' tilt from vertical, and where it sits
                // relative to the nearest L4 scoring trigger (the pipe's top section).
                var trig = Object.FindObjectsByType<Games.Reefscape.Scoring.Scorers.ReefPoleScorer>(FindObjectsSortMode.None)
                    .SelectMany(x => x.GetComponentsInChildren<BoxCollider>(true)).Where(b => b.name == "L4")
                    .OrderBy(b => (b.bounds.center - coral.transform.position).sqrMagnitude).First();
                var rel = trig.transform.InverseTransformPoint(coral.transform.position);
                L($"RESULT l4rest {(facing ? "front" : "back")} rep={rep} tilt={Vector3.Angle(coral.transform.forward, Vector3.up):F0}deg " +
                  $"coral-trigger(world)={(coral.transform.position - trig.bounds.center):F3} triggerSize={trig.bounds.size:F3} speed={crb.velocity.magnitude:F2}");
            }
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(2f);
        }

        if (Environment.GetEnvironmentVariable("TEST_SKIP_ALGAE") == "1") { L("DONE"); yield break; }

        // Processor: in front of the alliance's processor, facing it.
        var procScorer = Object.FindObjectsByType<Games.Reefscape.Scoring.Scorers.ProcessorScorer>(FindObjectsSortMode.None)
            .First(x => x.Alliance == _robot.Alliance);
        var proc = procScorer.transform.position;
        var bargeScorers = Object.FindObjectsByType<Games.Reefscape.Scoring.Scorers.BargeScorer>(FindObjectsSortMode.None)
            .Where(x => x.Alliance == _robot.Alliance).ToArray();
        var barges = bargeScorers.Select(x => x.transform.position).ToArray();
        // Where is the processor opening? Rays toward the wall at several heights and sideways offsets.
        {
            var (pb, _) = Box(procScorer, "scoreTrigger");
            var into = new Vector3(0, 0, Mathf.Sign(proc.z));
            for (var dx = -0.4f; dx <= 0.41f; dx += 0.2f)
            {
                var row = new System.Text.StringBuilder($"procProbe x={pb.x + dx:F2}: ");
                for (var y = 0.05f; y <= 0.81f; y += 0.1f)
                {
                    var o = new Vector3(pb.x + dx, y, proc.z - 0.6f * into.z);
                    row.Append(Physics.Raycast(o, into, out var hit, 1.2f, ~0, QueryTriggerInteraction.Ignore)
                        ? $"y{y:F2}->{hit.point.z:F2}({hit.collider.name}) " : $"y{y:F2}->open ");
                }
                L(row.ToString());
            }
        }
        L($"processor {proc:F2} barge scorers {string.Join(" ", barges.Select(b => b.ToString("F2")))}");
        {
            yield return LoadAlgae();
            var nrm = new Vector3(0, 0, -Mathf.Sign(proc.z)); // the processor opening faces into the field from the side wall
            var (pbox, _) = Box(procScorer, "scoreTrigger");
            PlaceRobot(new Vector3(proc.x, 0, proc.z) + nrm * 0.9f, -nrm); // the opening is centred on the processor, not its scoring slab
            yield return new WaitForSeconds(0.5f);
            yield return Tap(Key.U);
            yield return new WaitForSeconds(2.5f);
            L($"processor pre-release algae(world)={(NodeOf("Algae").controller ? NodeOf("Algae").controller.transform.position.ToString("F2") : "-")} proc={proc:F2} robot={_robot.transform.position:F2}");
            var before = Score();
            var held = NodeOf("Algae").controller;
            L($"overlap processor algae vs robot: {Overlaps(held)}");
            Keys(Key.K);
            var (pc, ps) = Box(procScorer, "scoreTrigger");
            yield return TraceRelease("procTrace", held, pc, ps);
            Keys();
            yield return new WaitForSeconds(1.5f);
            var after = Score();
            L($"RESULT processor released={!NodeOf("Algae").HasPiece()} netPoints+={after.net - before.net} algaeScored+={after.algae - before.algae}");
        }

        // Barge: from both sides of the barge, facing it and facing away.
        var bz = barges.Length > 0 ? barges[0] : Vector3.zero;
        foreach (var side in new[] { -1f, 1f })
        foreach (var facing in new[] { true, false })
        {
            yield return LoadAlgae();
            var toward = new Vector3(-side, 0, 0);
            PlaceRobot(new Vector3(bz.x + side * 1.0f, 0, bz.z), facing ? toward : -toward);
            yield return new WaitForSeconds(0.5f);
            yield return Tap(Key.P);
            // Like a driver: keep pushing up against the barge while the superstructure goes up.
            var bargeRb = _robot.GetComponent<Rigidbody>();
            for (var e = 0f; e < 3f; e += Time.fixedDeltaTime)
            {
                bargeRb.AddForce(toward * 400f);
                yield return new WaitForFixedUpdate();
            }
            var before = Score();
            var algae = NodeOf("Algae").controller;
            L($"overlap barge side={side} {(facing ? "facingBarge" : "facingAway")} algae vs robot: {Overlaps(algae)}");
            var clawT = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
            L($"barge pre-release side={side} {(facing ? "facingBarge" : "facingAway")} algae(world)={(algae ? algae.transform.position.ToString("F2") : "-")} net={bz:F2} " +
              $"robot={_robot.transform.position:F2} robotFwd={_robot.transform.forward:F2} clawFwd(world)={(clawT ? clawT.forward.ToString("F2") : "-")} clawUp(world)={(clawT ? clawT.up.ToString("F2") : "-")} {Status()}");
            Keys(Key.K);
            var (bc, bsz) = bargeScorers.Length > 0 ? BargeBox(bargeScorers[0]) : (Vector3.zero, Vector3.zero);
            yield return TraceRelease($"bargeTrace side={side} {(facing ? "facingBarge" : "facingAway")}", algae, bc, bsz);
            Keys();
            yield return new WaitForSeconds(1.5f);
            var after = Score();
            L($"RESULT barge side={side} {(facing ? "facingBarge" : "facingAway")} released={!NodeOf("Algae").HasPiece()} " +
              $"netPoints+={after.net - before.net} algaeScored+={after.algae - before.algae} " +
              $"algae(world)={(algae ? algae.transform.position.ToString("F2") : "-")}");
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(2f);
        }
        L("DONE");
    }

    // ---------------------------------------------------------------- coral station, robot off its line
    // Like a driver who is not lined up perfectly: the robot sits sideways / nearer / further from the chute. Logs
    // whether the coral ends in the claw, and if not, where it came to rest relative to the claw (up = above it).
    [UnityTest]
    [Timeout(600000)]
    public IEnumerator HpSweep()
    {
        yield return Boot();
        var t = _robot.transform;
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        var stations = Object.FindObjectsByType<Games.Reefscape.FieldScripts.CoralStation>(FindObjectsSortMode.None)
            .Where(s => s.Alliance == _robot.Alliance).ToArray();
        var spawn = (Transform)typeof(Games.Reefscape.FieldScripts.CoralStation).GetField("coralSpawnpoint", F).GetValue(stations[0]);
        var ee = (Field("endEffectorState") as RobotFramework.GamePieceSystem.GamePieceState)?.stateTarget;
        var sf = spawn.forward; sf.y = 0; sf.Normalize();
        var sr = Vector3.Cross(Vector3.up, sf);
        foreach (var facing in new[] { 1f, -1f })
        foreach (var (side, dist, yawOff) in new[] { (0f, 0.75f, 0f), (0.15f, 0.75f, 0f), (-0.15f, 0.75f, 0f), (0f, 0.6f, 0f), (0f, 0.95f, 0f), (0f, 0.75f, 12f), (0f, 0.75f, -12f) })
        {
            yield return DropHeld();
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1f);
            PlaceRobot(spawn.position + sf * dist + sr * side, Quaternion.AngleAxis(yawOff, Vector3.up) * (-sf * facing));
            yield return new WaitForSeconds(1f);
            Keys(Key.LeftShift);
            var end = Time.time + 5f;
            while (Time.time < end && !(NodeOf("Coral").HasPiece() && NodeOf("Coral").atTarget)) yield return new WaitForSeconds(0.1f);
            var held = NodeOf("Coral").HasPiece();
            var c = held ? NodeOf("Coral").controller : FreePiece("Coral", ee.position);
            var rel = c && ee ? ee.InverseTransformPoint(c.transform.position) : Vector3.zero;
            var touching = c ? string.Join(",", Physics.OverlapBox(c.GetComponentInChildren<Collider>().bounds.center, c.GetComponentInChildren<Collider>().bounds.extents + Vector3.one * 0.01f)
                .Where(x => x.transform.IsChildOf(t)).Select(x => x.transform.parent?.name + "/" + x.name).Distinct().Take(5)) : "";
            Keys();
            L($"RESULT hpSweep facing={facing} side={side:F2} dist={dist:F2} yaw={yawOff:F0} held={held} " +
              $"coral(claw-local)={rel:F2} worldAboveClaw={(c && ee ? c.transform.position.y - ee.position.y : 0):F2} touching=[{touching}]");
        }
        L("DONE");
    }

    // ---- driving back into the coral station in intake mode: which robot parts catch on the station (with pictures)
    [UnityTest]
    [Timeout(300000)]
    public IEnumerator HpView()
    {
        yield return Boot();
        var t = _robot.transform;
        var rb = _robot.GetComponent<Rigidbody>();
        if (_robot.CurrentCoralStationMode.DropType != DropType.Station) yield return Tap(Key.V);
        var station = Object.FindObjectsByType<Games.Reefscape.FieldScripts.CoralStation>(FindObjectsSortMode.None).First(s => s.Alliance == _robot.Alliance);
        var spawn = (Transform)typeof(Games.Reefscape.FieldScripts.CoralStation).GetField("coralSpawnpoint", F).GetValue(station);
        var sf = spawn.forward; sf.y = 0; sf.Normalize();
        var snapDir = Environment.GetEnvironmentVariable("SNAP_DIR") ?? "/home/seb/Desktop/MoSimulator/unity-tests-971/snaps_hp";
        System.IO.Directory.CreateDirectory(snapDir);
        // Pictures only with a graphics device (a -nographics run just logs the contacts).
        var graphics = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
        var cam = new GameObject("HpCam").AddComponent<Camera>();
        cam.fieldOfView = 50;
        var rt = graphics ? new RenderTexture(960, 640, 24) : null; cam.targetTexture = rt;
        var tex = new Texture2D(960, 640, TextureFormat.RGB24, false);
        void Snap(string name, Vector3 from, Vector3 at)
        {
            if (!graphics) return;
            cam.transform.position = from; cam.transform.LookAt(at); cam.Render();
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 960, 640), 0, 0); tex.Apply(); RenderTexture.active = null;
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(snapDir, name + ".png"), tex.EncodeToPNG());
        }
        var mine = _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled).ToArray();
        var pieces = LayerMask.GetMask("Coral", "Algae");
        foreach (var facing in new[] { 1f, -1f })
        {
            yield return DropHeld();
            PlaceRobot(spawn.position + sf * 1.6f, -sf * facing);
            yield return new WaitForSeconds(0.5f);
            // Back right up against the station (what the robot would hit first, swept straight in), then intake there.
            // (Swept with every robot body; the robot's own jointed parts don't count as hits.)
            var stationHit = _robot.GetComponentsInChildren<Rigidbody>()
                .SelectMany(b => b.SweepTestAll(-sf, 2f, QueryTriggerInteraction.Ignore))
                .Where(h => !h.collider.transform.IsChildOf(t) && h.collider.attachedRigidbody?.GetComponent<PieceCtrl>() == null)
                .OrderBy(h => h.distance).FirstOrDefault();
            var gapToStation = stationHit.collider ? stationHit.distance : 0f;
            L($"hpView facing={facing} sweep hit {(stationHit.collider ? stationHit.collider.transform.parent?.name + "/" + stationHit.collider.name : "-")} at {gapToStation:F2}m");
            PlaceRobot(t.position - sf * Mathf.Max(0, gapToStation - 0.01f), t.forward);
            yield return new WaitForSeconds(0.3f);
            Keys(Key.LeftShift);
            var worst = new System.Collections.Generic.Dictionary<string, float>();
            for (var e = 0f; e < 4f; e += Time.fixedDeltaTime)
            {
                rb.AddForce(-sf * 400f);
                yield return new WaitForFixedUpdate();
                foreach (var r in mine)
                foreach (var o in Physics.OverlapBox(r.bounds.center, r.bounds.extents + Vector3.one * 0.005f))
                {
                    if (o.isTrigger || o.transform.IsChildOf(t) || ((1 << o.gameObject.layer) & pieces) != 0) continue;
                    if (!Physics.ComputePenetration(r, r.transform.position, r.transform.rotation, o, o.transform.position, o.transform.rotation, out _, out var d)) continue;
                    var key = $"{r.transform.parent?.name}/{r.name} > {o.transform.parent?.name}/{o.name}";
                    worst[key] = Mathf.Max(worst.TryGetValue(key, out var w) ? w : 0, d);
                }
            }
            var side = Vector3.Cross(Vector3.up, sf);
            L($"RESULT hpView facing={facing} toSpawn={Vector3.Dot(spawn.position - t.position, sf):F2}m status={Status()}");
            foreach (var kv in worst.OrderByDescending(k => k.Value).Take(12)) L($"hpView facing={facing} contact {kv.Key} depth={kv.Value * 100:F1}cm");
            var focus = t.position + Vector3.up * 0.6f - sf * 0.3f;
            Snap($"hp_{(facing > 0 ? "front" : "back")}_side", focus + side * 1.6f + Vector3.up * 0.4f, focus);
            Snap($"hp_{(facing > 0 ? "front" : "back")}_top", focus + Vector3.up * 1.8f + sf * 0.3f, focus);
            Snap($"hp_{(facing > 0 ? "front" : "back")}_behind", focus + sf * 1.8f + Vector3.up * 0.6f, focus);
            Keys();
        }
        // The whole robot in the field's light, for comparing colours with photos.
        PlaceRobot(new Vector3(-2.2f, 0, 2.0f), Vector3.forward);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(2f);
        var c0 = t.position + Vector3.up * 0.5f;
        Snap("robot_front", c0 + t.forward * 2.2f + t.right * 0.6f + Vector3.up * 0.5f, c0);
        Snap("robot_side", c0 + t.right * 2.2f + Vector3.up * 0.4f, c0);
        Snap("robot_back", c0 - t.forward * 2.2f - t.right * 0.6f + Vector3.up * 0.5f, c0);
        Snap("robot_top", c0 + Vector3.up * 2.6f + t.forward * 0.4f, c0);
        // Every visible part and its materials, for repainting.
        foreach (var r in _robot.GetComponentsInChildren<MeshRenderer>())
        {
            var path = r.transform.name; for (var p = r.transform.parent; p && p != t; p = p.parent) path = p.name + "/" + path;
            var size = r.bounds.size;
            L($"paint {path} | {string.Join(",", r.sharedMaterials.Select(m => m ? m.name : "-"))} | size={size.x * 100:F0}x{size.y * 100:F0}x{size.z * 100:F0}cm");
        }
    }

    // ---------------------------------------------------------------- climber reach per deploy angle
    // CLIMB_PREP_ANGLES="75,90,105": for each climber deploy angle, where the climber's mouth (the gap between its
    // cage-grab wheels) and the cross standoff end up relative to the robot and its bumper.
    [UnityTest]
    [Timeout(600000)]
    public IEnumerator ClimberReach()
    {
        yield return Boot();
        var t = _robot.transform;
        var prep = Field("climbPrep") as ScriptableObject;
        var wheels = (Field("climberWheels") as Component[]).Where(w => w).ToArray();
        var standoff = ((Component)Field("climber")).GetComponentsInChildren<CapsuleCollider>(true).First(c => c.name == "Great Standoff");
        var bumper = _robot.GetComponentsInChildren<MeshRenderer>(true).First(r => r.name.StartsWith("Bumper Noodle")).bounds;
        foreach (var a in (Environment.GetEnvironmentVariable("CLIMB_PREP_ANGLES") ?? "75").Split(','))
        {
            var copy = Object.Instantiate(prep);
            copy.GetType().GetField("climberAngle").SetValue(copy, float.Parse(a, System.Globalization.CultureInfo.InvariantCulture));
            _robot.GetType().GetField("climbPrep", F).SetValue(_robot, copy);
            yield return Tap(Key.C);
            yield return new WaitForSeconds(2f);
            var mouth = t.InverseTransformPoint(wheels.Aggregate(Vector3.zero, (s, w) => s + w.transform.position) / wheels.Length);
            var stop = t.InverseTransformPoint(standoff.bounds.center);
            var bumperEdge = Mathf.Abs(t.InverseTransformPoint(bumper.max).x);
            L($"RESULT reach prep={a} mouth(robot-local)={mouth:F3} standoff={stop:F3} bumperEdgeX={bumperEdge:F3} mouthPastBumper={(Mathf.Abs(mouth.x) - bumperEdge) * 100:F1}cm standoffPastBumper={(Mathf.Abs(stop.x) - bumperEdge) * 100:F1}cm");
            // The climb key cycles Climb -> Climbed -> Stow.
            yield return Tap(Key.C);
            yield return new WaitForSeconds(1.5f);
            yield return Tap(Key.C);
            yield return new WaitForSeconds(1.5f);
        }
        L("DONE");
    }

    // ---------------------------------------------------------------- deep climb
    [UnityTest]
    [Timeout(600000)]
    public IEnumerator Climb()
    {
        yield return Boot();
        var t = _robot.transform;
        var climber = Field("climber") as Component;
        var trigger = climber ? climber.transform.Find("Cage Trigger") : null;
        L($"climb climber={(climber ? climber.name : "-")} trigger={(trigger ? "yes" : "no")}");
        // CLIMB_PREP=<deg>: try another deploy angle (in memory only; the setpoint asset is not saved).
        if (Environment.GetEnvironmentVariable("CLIMB_PREP") is { } prepAngle && _robot.GetType().GetField("climbPrep", F)?.GetValue(_robot) is { } prepSp)
            prepSp.GetType().GetField("climberAngle").SetValue(prepSp, float.Parse(prepAngle, System.Globalization.CultureInfo.InvariantCulture));
        var cages = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None)
            .Where(c => c.CompareTag("Cage") && c.attachedRigidbody).Select(c => c.attachedRigidbody).Distinct().ToArray();
        L($"cages {string.Join(" ", cages.Select(c => c.name + c.position.ToString("F2")))}");
        var drive = _robot.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
        var scorer = _robot.GetComponent<Games.Reefscape.Scoring.Scorers.ClimbScorer>();
        // Climbed angles to try (env CLIMB_ANGLES, e.g. "0,150"); each uses an in-memory copy of the setpoint.
        var baseClimb = Field("climbClimb") as ScriptableObject;
        var angles = (Environment.GetEnvironmentVariable("CLIMB_ANGLES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var angleText in angles.Length > 0 ? angles : new[] { "" })
        {
            var facing = 1f;
            if (angleText != "")
            {
                var copy = Object.Instantiate(baseClimb);
                copy.GetType().GetField("climberAngle").SetValue(copy, float.Parse(angleText, System.Globalization.CultureInfo.InvariantCulture));
                _robot.GetType().GetField("climbClimb", F).SetValue(_robot, copy);
                L($"climb angle={angleText}");
            }
            yield return Tap(Key.Z);
            yield return new WaitForSeconds(1.5f);
            // Nearest cage on the robot's alliance side; approach from that side of the barge.
            var cage = cages.OrderBy(c => (c.position - new Vector3(-2f, 0, 0)).sqrMagnitude).Skip(facing > 0 ? 0 : 1).First();
            var side = -1f; // approach from the -x side of the barge
            // Where the cage goes in: 971's cage trigger, or else the middle of the climber's clickers, which sit at
            // its mouth (other robots keep them in serialized fields of their climber script).
            var grabJoints = climber.GetType().GetFields(F).Select(fi => fi.GetValue(climber)).OfType<GenericAnimationJoint>()
                .Select(c => c.transform.position).ToArray();
            // Where the mouth is with the climber deployed (it moves when the climber swings out): deploy, measure, stow.
            yield return Tap(Key.C);
            yield return new WaitForSeconds(1.5f);
            // 971: the mouth is the gap between its cage-grab wheels, which is what a driver lines up on the post.
            var mouthWheels = (Field("climberWheels") as Component[])?.Where(w => w).Select(w => w.transform.position).ToArray() ?? new Vector3[0];
            var hook = mouthWheels.Length > 0 ? mouthWheels.Aggregate(Vector3.zero, (a, b) => a + b) / mouthWheels.Length
                : trigger ? trigger.position : grabJoints.Length > 0 ? grabJoints.Aggregate(Vector3.zero, (a, b) => a + b) / grabJoints.Length : climber.transform.position;
            L($"climb hook from {(mouthWheels.Length > 0 ? "climber wheels (mouth)" : trigger ? "trigger" : $"{grabJoints.Length} grab joints")} at robot-local {t.InverseTransformPoint(hook):F3} (deployed)");
            // Stays deployed (stow doesn't leave the climb), so the robot is placed with the climber already out.
            var hookLocal = t.InverseTransformPoint(hook); // where the hook sits on the robot
            // Turn the robot so its hook side points at the cage.
            var toCage = new Vector3(-side, 0, 0);
            var yaw = Mathf.Atan2(toCage.x, toCage.z) - Mathf.Atan2(hookLocal.x, hookLocal.z);
            var fwd = new Vector3(Mathf.Sin(yaw), 0, Mathf.Cos(yaw));
            var standoff = new Vector2(hookLocal.x, hookLocal.z).magnitude + 0.45f;
            // Line the climber's slot up on one of the cage's near posts (env CLIMB_POST_Z, default 0.076 m off centre).
            var postZ = float.Parse(Environment.GetEnvironmentVariable("CLIMB_POST_Z") ?? "0.076", System.Globalization.CultureInfo.InvariantCulture);
            PlaceRobot(new Vector3(cage.position.x + side * standoff, 0, cage.position.z + postZ), fwd);
            yield return new WaitForSeconds(0.5f);
            if (mouthWheels.Length > 0)
            {
                // Line the mouth up on the nearest real cage post (the cage hangs at an angle, so its origin is not a
                // post), 30 cm out from it, like a driver lining up before driving in.
                var mouthNow = t.TransformPoint(hookLocal);
                bool Vertical(CapsuleCollider c) =>
                    Mathf.Abs(Vector3.Dot(c.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[c.direction]).normalized, Vector3.up)) > 0.8f;
                var post = cage.GetComponentsInChildren<CapsuleCollider>().Where(c => !c.isTrigger && Vertical(c))
                    .OrderBy(c => Mathf.Round((c.bounds.center.x - mouthNow.x) * -side * 10f)).ThenBy(c => Mathf.Abs(c.bounds.center.z - mouthNow.z)).First();
                var want = new Vector3(post.bounds.center.x + side * 0.30f, mouthNow.y, post.bounds.center.z);
                var shift = want - mouthNow; shift.y = 0;
                PlaceRobot(t.position + shift, t.forward);
                L($"climb lined up on vertical post {post.name} at {post.bounds.center:F2} (of {cage.GetComponentsInChildren<CapsuleCollider>().Count(c => !c.isTrigger && Vertical(c))} vertical), mouth now {t.TransformPoint(hookLocal):F2}");
            }
            yield return new WaitForSeconds(1.5f); // (already deployed above)
            L($"climb deployed facing={facing} {Status()} hook(robot-local)={t.InverseTransformPoint(trigger ? trigger.position : hook):F2} cage={cage.position:F2}");
            // Drive into the cage like a player until it sits in the climber, then press climb again.
            var rb = _robot.GetComponent<Rigidbody>();
            var end = Time.time + 8f;
            var cageColliders = cage.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
            var backStop = climber.GetComponentsInChildren<CapsuleCollider>(true).FirstOrDefault(c => c.name == "Great Standoff");
            // Where the grabber's parts are relative to the cage (world axes, cage origin), and the posts.
            void Geom(string when)
            {
                foreach (var c in climber.GetComponentsInChildren<BoxCollider>())
                {
                    var b = c.bounds;
                    L($"climb geom[{when}] {c.transform.parent.name}/{c.name} trig={c.isTrigger} min={(b.min - cage.position):F3} max={(b.max - cage.position):F3}");
                }
                foreach (var c in cageColliders.OfType<CapsuleCollider>())
                    L($"climb geom[{when}] post {c.name} centre={(c.bounds.center - cage.position):F3}");
                L($"climb geom[{when}] robotYaw={t.eulerAngles.y:F1} robot={(t.position - cage.position):F3}");
            }
            Geom("deployed");
            if (backStop)
            {
                var ax = backStop.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[backStop.direction]).normalized;
                L($"climb standoff axis={ax:F2} fromVertical={Vector3.Angle(ax, Vector3.up):F0} (robot-local {t.InverseTransformDirection(ax):F2}) climberLocalEuler={climber.transform.localEulerAngles:F0}");
            }
            {
                var wb = climber.GetComponentsInChildren<CapsuleCollider>(true).Where(c => c.name.Contains("Wheel Body")).ToArray();
                Vector3 Ax(CapsuleCollider c) => c.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[c.direction]).normalized;
                float R(CapsuleCollider c) => c.radius * Mathf.Max(Mathf.Abs(c.transform.lossyScale.x), Mathf.Abs(c.transform.lossyScale.y), Mathf.Abs(c.transform.lossyScale.z));
                var p0 = cageColliders.OfType<CapsuleCollider>().First();
                if (wb.Length == 2)
                {
                    var d = Vector3.ProjectOnPlane(wb[1].bounds.center - wb[0].bounds.center, Ax(wb[0])).magnitude;
                    L($"climb wheels centreDist={d * 100:F1}cm radii={R(wb[0]) * 100:F1},{R(wb[1]) * 100:F1}cm gap={(d - R(wb[0]) - R(wb[1])) * 100:F1}cm postDia={2 * R(p0) * 100:F1}cm");
                }
                foreach (var c in climber.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger))
                    if (c is CapsuleCollider cc) L($"climb capsule {c.name} r={R(cc) * 100:F1}cm axisLocal={t.InverseTransformDirection(Ax(cc)):F2} centreLocal={t.InverseTransformPoint(c.bounds.center):F3}");
                foreach (var c in climber.GetComponentsInChildren<BoxCollider>(true).Where(c => !c.isTrigger && c.name.Contains("plate") && !c.name.Contains("Latcher")))
                    L($"climb plate {c.name} centreLocal={t.InverseTransformPoint(c.transform.TransformPoint(c.center)):F3} size={Vector3.Scale(c.size, c.transform.lossyScale):F3} normalLocal={t.InverseTransformDirection(c.transform.up):F2}");
            }
            // CLIMB_SWEEP=1: how far the grab wheels' axles (normal to the climber's plates) lean from vertical at a
            // range of climber targets.
            if (Environment.GetEnvironmentVariable("CLIMB_SWEEP") == "1")
            {
                var prep = _robot.GetType().GetField("climbPrep", F).GetValue(_robot);
                var target = prep.GetType().GetField("climberAngle");
                var was = target.GetValue(prep);
                var axle = climber.GetComponentsInChildren<CapsuleCollider>(true).First(c => c.name.Contains("Wheel Body"));
                for (var a = 0f; a <= 180f; a += 15f)
                {
                    target.SetValue(prep, a);
                    yield return new WaitForSeconds(1.2f);
                    var dir = axle.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[axle.direction]).normalized;
                    var lean = Vector3.SignedAngle(Vector3.up, dir.y < 0 ? -dir : dir, t.forward);
                    L($"climb sweep target={a:F0} axle={dir:F2} lean={lean:F0} mouthY={t.InverseTransformPoint(climber.GetComponentsInChildren<GenericRoller>().Aggregate(Vector3.zero, (v, r) => v + r.transform.position) / 2):F2}");
                }
                target.SetValue(prep, was);
                yield break;
            }
            // CLIMB_SNAPS=1 (run with graphics): pictures of the climber and cage from above and from behind.
            Camera snapCam = null; RenderTexture snapRt = null; Texture2D snapTex = null;
            var snapDir = Environment.GetEnvironmentVariable("SNAP_DIR") ?? "/home/seb/Desktop/MoSimulator/unity-tests-971/snaps";
            if (Environment.GetEnvironmentVariable("CLIMB_SNAPS") == "1")
            {
                System.IO.Directory.CreateDirectory(snapDir);
                snapCam = new GameObject("ClimbCam").AddComponent<Camera>();
                snapCam.fieldOfView = 45;
                snapRt = new RenderTexture(960, 640, 24);
                snapCam.targetTexture = snapRt;
                snapTex = new Texture2D(960, 640, TextureFormat.RGB24, false);
            }
            void ClimbSnap(string name)
            {
                if (!snapCam) return;
                var focus = climber.GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((a, b) => { a.Encapsulate(b); return a; }).center;
                foreach (var (view, dir, dist) in new[] { ("top", new Vector3(0, 1, 0.02f), 1.1f), ("behind", new Vector3(0, 0.25f, -1), 1.2f), ("out", new Vector3(side, 0.35f, 0), 1.3f) })
                {
                    snapCam.transform.position = focus + dir.normalized * dist;
                    snapCam.transform.LookAt(focus);
                    snapCam.Render();
                    RenderTexture.active = snapRt;
                    snapTex.ReadPixels(new Rect(0, 0, 960, 640), 0, 0);
                    snapTex.Apply();
                    RenderTexture.active = null;
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(snapDir, $"climb_{name}_{view}.png"), snapTex.EncodeToPNG());
                }
            }
            ClimbSnap("0deployed");
            var approachSnaps = 0;
            // A post seated against the standoff inside the V's channel (within 4 cm of its centre line).
            bool PostAtBackStop() => backStop && cageColliders.OfType<CapsuleCollider>().Any(c =>
                Mathf.Abs(Vector3.Dot(c.bounds.center - backStop.bounds.center,
                    backStop.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[backStop.direction]).normalized)) < 0.04f &&
                Physics.ComputePenetration(backStop, backStop.transform.position, backStop.transform.rotation, c, c.transform.position, c.transform.rotation, out _, out _));
            // CLIMB_PLAN=1: the climber's colliders flattened into the plates' plane (u: from the standoff toward the mouth,
            // v: along the standoff), with the cage posts, for a drawing.
            void Plan(string when)
            {
                if (Environment.GetEnvironmentVariable("CLIMB_PLAN") != "1" || !backStop) return;
                var o = backStop.bounds.center;
                var vAx = backStop.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[backStop.direction]).normalized;
                var wheelsMid = climber.GetComponentsInChildren<GenericRoller>().Aggregate(Vector3.zero, (a, r) => a + r.transform.position) / 2;
                var uAx = Vector3.ProjectOnPlane(wheelsMid - o, vAx).normalized;
                var nAx = Vector3.Cross(uAx, vAx);
                string P(Vector3 w) { var d = w - o; return $"{Vector3.Dot(d, uAx):F4},{Vector3.Dot(d, vAx):F4},{Vector3.Dot(d, nAx):F4}"; }
                foreach (var c in climber.GetComponentsInChildren<Collider>(true).Where(c => c.enabled))
                {
                    if (c is BoxCollider bx)
                    {
                        var corners = new System.Collections.Generic.List<string>();
                        for (var i = 0; i < 8; i++)
                            corners.Add(P(bx.transform.TransformPoint(bx.center + Vector3.Scale(bx.size / 2, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1)))));
                        L($"climb plan[{when}] box {c.transform.parent.name}/{c.name} trig={c.isTrigger} {string.Join(" ", corners)}");
                    }
                    else if (c is CapsuleCollider cc)
                    {
                        var ax = cc.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[cc.direction]).normalized;
                        var sc = Mathf.Max(Mathf.Abs(cc.transform.lossyScale.x), Mathf.Abs(cc.transform.lossyScale.y), Mathf.Abs(cc.transform.lossyScale.z));
                        var half = Mathf.Max(0, cc.height * sc / 2 - cc.radius * sc);
                        var ctr = cc.transform.TransformPoint(cc.center);
                        L($"climb plan[{when}] capsule {c.name} r={cc.radius * sc:F4} {P(ctr - ax * half)} {P(ctr + ax * half)}");
                    }
                }
                // An orthographic picture straight down the plates' normal, 30 cm across, standoff in the middle.
                if (snapCam)
                {
                    foreach (var (view, dir) in new[] { ("planN", nAx), ("planS", -nAx) })
                    {
                        snapCam.orthographic = true;
                        snapCam.orthographicSize = 0.15f;
                        snapCam.nearClipPlane = 0.01f;
                        snapCam.transform.position = o + uAx * 0.08f + dir * 0.5f;
                        snapCam.transform.rotation = Quaternion.LookRotation(-dir, uAx);
                        snapCam.Render();
                        RenderTexture.active = snapRt;
                        snapTex.ReadPixels(new Rect(0, 0, 960, 640), 0, 0);
                        snapTex.Apply();
                        RenderTexture.active = null;
                        System.IO.File.WriteAllBytes(System.IO.Path.Combine(snapDir, $"climb_{view}_{when}.png"), snapTex.EncodeToPNG());
                    }
                    snapCam.orthographic = false;
                }
                foreach (var cc in cageColliders.OfType<CapsuleCollider>())
                {
                    var ax = cc.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[cc.direction]).normalized;
                    var sc = Mathf.Max(Mathf.Abs(cc.transform.lossyScale.x), Mathf.Abs(cc.transform.lossyScale.y), Mathf.Abs(cc.transform.lossyScale.z));
                    var half = Mathf.Max(0, cc.height * sc / 2 - cc.radius * sc);
                    var ctr = cc.transform.TransformPoint(cc.center);
                    L($"climb plan[{when}] post {cc.name} r={cc.radius * sc:F4} {P(ctr - ax * half)} {P(ctr + ax * half)}");
                }
            }
            Plan("deployed");
            // Keep driving in until the post is all the way into the grabber (or the cage is in the trigger, without one).
            // A driver holds the line: no sideways slide and no turning while backing in (test only).
            var heldConstraints = rb.constraints;
            rb.constraints |= RigidbodyConstraints.FreezePositionZ | RigidbodyConstraints.FreezeRotationY;
            while (Time.time < end && _robot.CurrentSetpoint != Games.Reefscape.Enums.ReefscapeSetpoints.Climbed && !(backStop ? PostAtBackStop() : scorer.ScoringTriggered))
            {
                // Push like a drivetrain would, so the cage and the grabber's colliders actually stop the robot; hold the
                // line like a driver (no sideways slide, no turning) so the post goes into the mouth it was lined up on.
                rb.AddForce(new Vector3(-side * 600f, 0, 0));
                rb.velocity = new Vector3(rb.velocity.x, rb.velocity.y, 0);
                rb.angularVelocity = new Vector3(rb.angularVelocity.x, 0, rb.angularVelocity.z);
                yield return new WaitForFixedUpdate();
                if (Time.frameCount % 25 == 0)
                {
                    // What each clicker's MoveAway trigger overlaps (as GenericAnimationJoint queries it), and the cage's yaw.
                    var seen = climber.GetComponentsInChildren<GenericAnimationJoint>().Select(c =>
                    {
                        var away = (BoxCollider)typeof(GenericAnimationJoint).GetField("moveAwayCollider", F).GetValue(c);
                        if (!away) return $"{c.name}:{c.transform.localEulerAngles.y:F0}";
                        var hits = Physics.OverlapBox(away.transform.position, Vector3.Scale(away.size, away.transform.lossyScale) / 2, away.transform.rotation)
                            .Where(h => h.gameObject.layer != c.gameObject.layer).Select(h => h.name);
                        // Flap tip (end of the flap collider, local +z) relative to the target post, in world axes.
                        var flap = c.transform.Find("Colliders")?.GetComponentInChildren<BoxCollider>();
                        if (!flap) return $"{c.name}:{c.transform.localEulerAngles.y:F0}[{string.Join("|", hits)}]";
                        var tip = flap.transform.TransformPoint(flap.center + new Vector3(0, 0, flap.size.z / 2));
                        var post = cageColliders.OfType<CapsuleCollider>().OrderBy(p => (p.bounds.center - tip).sqrMagnitude).First();
                        var rel = tip - post.bounds.center;
                        return $"{c.name}:{c.transform.localEulerAngles.y:F0}[{string.Join("|", hits)}] tip-post=({rel.x:F3},{rel.z:F3})";
                    });
                    L($"climb approach x={t.position.x:F2} v={rb.velocity.x:F2} cageYaw={cage.rotation.eulerAngles.y:F0} {string.Join(" ", seen)}");
                    if (approachSnaps < 8 && Time.frameCount % 75 == 0) ClimbSnap($"1approach{approachSnaps++}");
                }
            }
            rb.constraints = heldConstraints;
            Plan("approached");
            // Which of the robot's colliders touch the cage (within 1.5 cm) where the approach stopped.
            foreach (var rc in _robot.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled && !(c is MeshCollider m && !m.convex)))
            foreach (var cc in cageColliders)
            {
                var p = cc.ClosestPoint(rc.bounds.center);
                var gap = Vector3.Distance(rc.ClosestPoint(p), p);
                if (gap < 0.015f) L($"climb contact {rc.transform.parent.name}/{rc.name} ({rc.GetType().Name}) ~ {cc.name} gap={gap * 100:F1}cm at {(p - t.position):F3}");
            }
            Geom("approached");
            ClimbSnap("2approached");
            L($"climb cageInClimber={scorer.ScoringTriggered} postAtBackStop={PostAtBackStop()} robot={t.position:F2} " +
              $"clickers=[{string.Join(",", climber.GetComponentsInChildren<GenericAnimationJoint>().Select(c => c.transform.localEulerAngles.y.ToString("F0")))}]");
            // Climbed: the climber swings back and lifts the robot (some robots switch to it on their own).
            if (_robot.CurrentSetpoint != Games.Reefscape.Enums.ReefscapeSetpoints.Climbed) yield return Tap(Key.C);
            for (var k = 0; k < 16; k++)
            {
                yield return new WaitForSeconds(0.5f);
                var cj = climber.GetComponents<ConfigurableJoint>();
                var hookBox = trigger ? trigger.GetComponent<BoxCollider>() : null;
                // Robot colliders actually touching (overlapping) a cage collider.
                var cageCols = cage.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
                var near = _robot.GetComponentsInChildren<Collider>().Where(r => !r.isTrigger && r.enabled)
                    .SelectMany(r => cageCols.Where(c => r.bounds.Intersects(c.bounds) && Physics.ComputePenetration(
                            r, r.transform.position, r.transform.rotation, c, c.transform.position, c.transform.rotation, out _, out _))
                        .Select(c => $"{r.transform.parent?.name}/{r.name}>{c.name}@{c.ClosestPoint(r.bounds.center).y:F2}"))
                    .Distinct().Take(8);
                L($"climbing k={k} robotY={t.position.y:F3} robotTilt={Vector3.Angle(t.up, Vector3.up):F1} touchingGround={drive.IsTouchingGround} " +
                  $"cage={cage.position:F2} cageVel={cage.velocity:F2} hook={(hookBox ? hookBox.bounds.center.ToString("F2") : "-")} " +
                  $"climberJointForce={(cj.Length > 0 ? cj[0].currentForce.magnitude : 0):F0} cageInClimber={scorer.ScoringTriggered} " +
                  $"robotPartsNearCage=[{string.Join(",", near)}] climber={((Component)climber).transform.localEulerAngles:F0} " +
                  $"clickers=[{string.Join(",", climber.GetComponentsInChildren<GenericAnimationJoint>().Select(c => c.transform.localEulerAngles.y.ToString("F0")))}]");
                if (k < 4) ClimbSnap($"3climbing{k}");
            }
            L($"RESULT climb facing={facing} climbed={_robot.CurrentSetpoint == Games.Reefscape.Enums.ReefscapeSetpoints.Climbed} " +
              $"offGround={!drive.IsTouchingGround} cageInTrigger={scorer.ScoringTriggered} robotY={t.position.y:F2}");
            yield return Tap(Key.C); // back to stow: lets go of the cage
            yield return new WaitForSeconds(2f);
            yield return new WaitForSeconds(2f);
        }
        L("DONE");
    }
}
#endif
