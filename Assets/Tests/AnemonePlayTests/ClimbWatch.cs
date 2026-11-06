// Watches a robot climb a deep cage and saves camera frames (needs graphics: run without -nographics).
// CLIMB_META = robot metadata asset (default Anemone), CLIMB_SIDES = robot sides to bring to the cage (front,back,left,
// right; default all four), CLIMB_SHOTS = output folder. Logs "[CW]" lines with lift, tilt, ground contact and the
// climb trigger for each try.
//   Unity -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter ClimbWatch -testResults r.xml -logFile l.log
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.Robots;
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

public class ClimbWatch
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    ReefscapeRobotBase _robot;
    Keyboard _kb;
    Key[] _held = Array.Empty<Key>();
    Vector3 _wDir, _dDir;
    Camera _cam;
    RenderTexture _rt;
    string _dir;
    int _frame;
    float? _angle;

    static void L(string s) => Debug.Log("[CW] " + s);

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

    void PlaceRobot(Vector3 pos, Vector3 forward)
    {
        var t = _robot.transform;
        forward.y = 0;
        pos.y = t.position.y;
        foreach (var rb in _robot.GetComponentsInChildren<Rigidbody>())
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        t.SetPositionAndRotation(pos, Quaternion.LookRotation(forward.normalized, Vector3.up));
        Physics.SyncTransforms();
    }

    // drive keys toward a world direction (field-oriented drive, probed at the start)
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

    // a close-up of the climber's hook, from in front of the robot and above, looking at the climber pivot
    void Close(string tag)
    {
        var climber = _robot.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "Climber");
        if (!climber) return;
        var t = _robot.transform;
        var c = climber.position - t.right * 0.25f + t.up * 0.15f;
        var p = _cam.transform.position;
        var r = _cam.transform.rotation;
        _cam.transform.position = c + t.forward * 0.9f + t.up * 0.5f - t.right * 0.2f;
        _cam.transform.LookAt(c);
        Shot("close_" + tag);
        _cam.transform.SetPositionAndRotation(p, r);
    }

    // where each hook box is against the cage: the nearest cage collider and the gap to it (negative = inside)
    bool _hookTouching;
    string _dirSuffix = "";

    IEnumerator WatchHook(Rigidbody cage)
    {
        var climber = _robot.GetComponentsInChildren<Transform>(true).First(x => x.name == "Climber");
        var hooks = climber.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
        var cageCols = cage.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && !(c is MeshCollider)).ToArray();
        for (var i = 0; i < 60 && !_hookTouching; i++)
        {
            _hookTouching = hooks.Any(h => cageCols.Any(cc => (cc.ClosestPoint(h.bounds.center) - h.ClosestPoint(cc.ClosestPoint(h.bounds.center))).magnitude < 0.004f));
            yield return new WaitForFixedUpdate();
        }
    }

    // Drives one way at a steady crawl (m/s): the drive's response to its multiplier isn't proportional at low speed, so
    // the multiplier is steered by the measured speed. Leaves the keys held and the multiplier set; the caller lets go.
    float _creepMp;
    IEnumerator Creep(RobotFramework.Controllers.Drivetrain.DriveController dc, Key[] keys, Vector3 dir, float speed, Func<bool> done, float timeout, string shot)
    {
        var rb = _robot.GetComponent<Rigidbody>();
        Hold(keys);
        _creepMp = 0.1f;
        dc.SetDriveMp(_creepMp);
        var end = Time.time + timeout;
        var shotAt = 0f;
        while (!done() && Time.time < end)
        {
            yield return new WaitForFixedUpdate();
            _creepMp = Mathf.Clamp(_creepMp + 0.004f * (speed - Vector3.Dot(rb.velocity, dir)) / speed, 0.02f, 0.6f);
            dc.SetDriveMp(_creepMp);
            if (Time.time > shotAt) { shotAt = Time.time + 0.25f; Shot(shot); }
        }
    }

    // the gap from any of the climber's colliders to the cage
    float HookGap(Rigidbody cage)
    {
        var climber = _robot.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "Climber");
        if (!climber) return 9f;
        var cageCols = cage.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && !(c is MeshCollider)).ToArray();
        var best = 9f;
        foreach (var h in climber.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger))
        foreach (var cc in cageCols)
        {
            var gap = Physics.ComputePenetration(h, h.transform.position, h.transform.rotation, cc, cc.transform.position, cc.transform.rotation, out _, out var depth)
                ? -depth : (cc.ClosestPoint(h.bounds.center) - h.ClosestPoint(cc.bounds.center)).magnitude;
            best = Mathf.Min(best, gap);
        }
        return best;
    }

    string Hook(Rigidbody cage)
    {
        var climber = _robot.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "Climber");
        if (!climber) return "";
        var cageCols = cage.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && !(c is MeshCollider)).ToArray();
        var parts = new List<string>();
        foreach (var h in climber.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger))
        {
            var best = float.MaxValue; string near = "";
            foreach (var cc in cageCols)
            {
                var gap = Physics.ComputePenetration(h, h.transform.position, h.transform.rotation, cc, cc.transform.position, cc.transform.rotation, out _, out var depth)
                    ? -depth : (cc.ClosestPoint(h.bounds.center) - h.ClosestPoint(cc.bounds.center)).magnitude;
                if (gap < best) { best = gap; near = cc.name + (cc is CapsuleCollider ? "(post)" : ""); }
            }
            parts.Add($"{h.name}:{best * 100:F1}cm@{near}");
        }
        return string.Join(" ", parts);
    }

    void Shot(string tag)
    {
        _cam.Render();
        RenderTexture.active = _rt;
        var tex = new Texture2D(_rt.width, _rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, _rt.width, _rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(Path.Combine(_dir, $"{_frame++:000}_{tag}.png"), tex.EncodeToPNG());
        Object.Destroy(tex);
    }

    Bounds CageBounds(Rigidbody cage)
    {
        var cs = cage.GetComponentsInChildren<Collider>();
        var b = cs[0].bounds;
        foreach (var c in cs) b.Encapsulate(c.bounds);
        return b;
    }

    string State(ClimbScorer cs, RobotFramework.Controllers.Drivetrain.DriveController dc) =>
        $"sp={_robot.CurrentSetpoint} robotY={_robot.transform.position.y:F3} tilt={Vector3.Angle(_robot.transform.up, Vector3.up):F0} " +
        $"touchingGround={dc.IsTouchingGround} cageTriggered={cs.ScoringTriggered} autoClimb={cs.AutoClimbTriggered}";

    IEnumerator Try(string side, Rigidbody cage)
    {
        // reset: stow
        Keys();
        if (_robot.CurrentSetpoint is Games.Reefscape.Enums.ReefscapeSetpoints.Climb or Games.Reefscape.Enums.ReefscapeSetpoints.Climbed)
        {
            yield return Tap(Key.C);
            yield return new WaitForSeconds(0.5f);
        }
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1.5f);

        var cb = CageBounds(cage);
        var ourSide = Mathf.Sign(Object.FindObjectsByType<Reef>(FindObjectsSortMode.None).First(r => r.Alliance == _robot.Alliance).transform.position.x);
        var away = new Vector3(ourSide, 0, 0); // from the cage toward our side of the field
        // the robot side that should meet the cage points back toward it
        var toCage = -away;
        var fwd = side switch
        {
            "front" => toCage,
            "back" => -toCage,
            // right = Cross(up, forward): the left side meets the cage when right points away from it
            "left" => Vector3.Cross(-toCage, Vector3.up),
            _ => Vector3.Cross(toCage, Vector3.up),
        };
        PlaceRobot(new Vector3(cb.center.x, 0, cb.center.z) + away * 1.25f, fwd);
        // Anemone: line the climber arm up with the cage's middle and try the given climbed angle
        var climbedField = _robot.GetType().GetField("climbed", F);
        float? oldAngle = null;
        if (_angle.HasValue && climbedField?.GetValue(_robot) is Prefabs.Reefscape.Robots.Mods.SPAM._180.AnemoneSetpoint sp)
        {
            oldAngle = sp.climberAngle;
            sp.climberAngle = _angle.Value;
        }
        // CLIMB_PREP: the deployed climber angle to try (the climb-prep setpoint)
        var prepField = _robot.GetType().GetField("climbPrep", F);
        float? oldPrep = null;
        if (float.TryParse(Environment.GetEnvironmentVariable("CLIMB_PREP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var prepAngle) &&
            prepField?.GetValue(_robot) is Prefabs.Reefscape.Robots.Mods.SPAM._180.AnemoneSetpoint psp)
        {
            oldPrep = psp.climberAngle;
            psp.climberAngle = prepAngle;
            _dirSuffix = "_prep" + prepAngle.ToString("F0");
        }
        var trig = _robot.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == "Cage Trigger");
        if (trig && (side == "left" || side == "right"))
        {
            var along = Vector3.Dot(cb.center - trig.position, fwd);
            PlaceRobot(_robot.transform.position + fwd * along, fwd);
        }
        _dir = Path.Combine(Environment.GetEnvironmentVariable("CLIMB_SHOTS") ?? "Temp/ClimbWatch",
            _robot.GetType().Name + "_" + side + (_angle.HasValue ? "_" + _angle.Value.ToString("F0") : "") + _dirSuffix);
        Directory.CreateDirectory(_dir);
        _frame = 0;
        // camera off to the side of the approach line, looking at the cage
        var sideDir = Vector3.Cross(Vector3.up, away).normalized;
        _cam.transform.position = cb.center + sideDir * 2.6f + away * 0.6f + Vector3.up * 0.6f;
        _cam.transform.LookAt(cb.center + away * 0.6f);
        var cs = _robot.GetComponent<ClimbScorer>();
        var dc = _robot.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
        yield return new WaitForSeconds(0.5f);
        Shot("start");
        // CLIMB_VIDEO=dir: the whole try as a clip from the same spot (30 fps of game time, one PNG per frame)
        AnemoneRecorder rec = null;
        var videoDir = Environment.GetEnvironmentVariable("CLIMB_VIDEO");
        if (!string.IsNullOrEmpty(videoDir))
        {
            var anchor = new GameObject("Climb Video Anchor").transform;
            anchor.position = cb.center;
            Time.captureFramerate = 30;
            rec = new GameObject("Climb Rec").AddComponent<AnemoneRecorder>();
            rec.Init(Path.Combine(videoDir, side + (_angle.HasValue ? "_" + _angle.Value.ToString("F0") : "")), anchor,
                away * 0.6f + Vector3.up * 0.1f, sideDir * 2.6f + Vector3.up * 0.5f, 2.7f);
        }
        yield return Tap(Key.C); // Climb: deploy
        for (var k = 0; k < 10; k++) { yield return new WaitForSeconds(0.3f); Shot("deploy"); }
        L($"{side} deployed: {State(cs, dc)}");
        // As a driver climbs: slowly sideways with the arm (lined up above) going between two of the cage's uprights,
        // stopping with the bumper just off the cage; then slowly toward the robot's front until an upright leans on the arm.
        var start = _robot.transform.position;
        // where the uprights and the arm are in the robot's frame (x = out to the left is negative, z = forward)
        var armBar = _robot.GetComponentsInChildren<Collider>(true).First(c => c.name == "Arm Bar");
        L($"{side} line-up: arm z {_robot.transform.InverseTransformPoint(armBar.bounds.center).z:F3} uprights (x,z): " +
          string.Join(" ", cage.GetComponentsInChildren<CapsuleCollider>().Select(c => { var l = _robot.transform.InverseTransformPoint(c.bounds.center); return $"({l.x:F3},{l.z:F3})"; })));
        var keys = DriveKeys(toCage);
        float NearFace()
        {
            var c = CageBounds(cage);
            return Mathf.Abs(_robot.transform.InverseTransformPoint(c.center).x) - c.extents.x;
        }
        const float BumperSide = 0.462f;
        // the near upright has to end inboard of the top lip (19 cm out along the arm), so that going forward brings it
        // against the arm bar, between the lip and the lower plate: just off the bumper
        // CLIMB_FACE: how close the cage's near face comes to the robot's centre (m)
        var faceAt = float.TryParse(Environment.GetEnvironmentVariable("CLIMB_FACE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cf) ? cf : BumperSide + 0.013f;
        yield return Creep(dc, keys, toCage, 0.08f, () => NearFace() <= faceAt, 20f, "approach");
        Release(keys);
        dc.SetDriveMp(1f);
        yield return new WaitForSeconds(0.6f);
        L($"{side} arm between the uprights: cage near face {NearFace() * 100:F1} cm from the robot's centre, side moved {(_robot.transform.position - start).magnitude:F2} | {Hook(cage)}");
        Shot("between");
        Close("between");
        var cage0 = cage.position;
        var robotFwd = _robot.transform.forward;
        keys = DriveKeys(robotFwd);
        var t1 = Time.time;
        var touchedAt = -1f;
        // until the upright has been leaning on the arm for a moment. CLIMB_LEAN: how long a moment (s)
        var lean = float.TryParse(Environment.GetEnvironmentVariable("CLIMB_LEAN"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ln) ? ln : 0.5f;
        yield return Creep(dc, keys, robotFwd, 0.1f, () =>
        {
            if (touchedAt < 0 && HookGap(cage) < 0.005f) touchedAt = Time.time;
            return touchedAt >= 0 && Time.time - touchedAt > lean;
        }, 8f, "lean");
        // CLIMB_PUSH=1: keep creeping forward through the climb, the upright held against the arm
        var push = Environment.GetEnvironmentVariable("CLIMB_PUSH") == "1";
        if (!push)
        {
            Release(keys);
            dc.SetDriveMp(1f);
        }
        yield return new WaitForSeconds(0.3f);
        _hookTouching = touchedAt >= 0;
        L($"{side} upright leaning: touched={_hookTouching} after {(touchedAt < 0 ? -1f : touchedAt - t1):F2} s, cage moved {(cage.position - cage0).magnitude * 100:F1} cm, " +
          $"uprights (x,z): " + string.Join(" ", cage.GetComponentsInChildren<CapsuleCollider>().Select(c => { var l = _robot.transform.InverseTransformPoint(c.ClosestPoint(armBar.bounds.center)); return $"({l.x:F3},{l.z:F3})"; })));
        L($"{side} at cage: {State(cs, dc)} moved={(_robot.transform.position - start).magnitude:F2} hookTouching={_hookTouching}");
        L($"{side} hook at cage: {Hook(cage)}");
        {
            // the cage and the hook in the climber's frame (m): x = out from the robot side, y = up, z = forward
            var climber = _robot.GetComponentsInChildren<Transform>(true).First(x => x.name == "Climber");
            string In(Collider c)
            {
                var b = c.bounds;
                var lo = climber.InverseTransformPoint(b.min); var hi = climber.InverseTransformPoint(b.max);
                var mn = Vector3.Min(lo, hi); var mx = Vector3.Max(lo, hi);
                return $"{c.name}{(c is CapsuleCollider ? "(post)" : "")}[{mn.x:F2}..{mx.x:F2} {mn.y:F2}..{mx.y:F2} {mn.z:F2}..{mx.z:F2}]";
            }
            L($"{side} cage in climber frame: {string.Join(" ", cage.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && !(c is MeshCollider)).Select(In))}");
            L($"{side} hook in climber frame: {string.Join(" ", climber.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).Select(In))} climberZ={climber.localEulerAngles.z:F0}");
        }
        Shot("atcage");
        Close("atcage");
        yield return Tap(Key.C); // Climbed
        var lifted = false;
        for (var k = 0; k < 72; k++)
        {
            yield return new WaitForSeconds(0.25f);
            Shot("climb");
            if (k % 2 == 1) Close("climb");
            lifted |= cs.ScoringTriggered && !dc.IsTouchingGround;
            if (k % 4 == 3) L($"{side} climb +{(k + 1) * 0.25f:F2}s {State(cs, dc)} climber={_robot.GetComponentsInChildren<Transform>(true).First(x => x.name == "Climber").localEulerAngles.z:F0} | {Hook(cage)}");
        }
        if (push)
        {
            Release(keys);
            dc.SetDriveMp(1f);
        }
        if (rec) { Object.Destroy(rec.gameObject); Time.captureFramerate = 0; }
        if (oldAngle.HasValue) ((Prefabs.Reefscape.Robots.Mods.SPAM._180.AnemoneSetpoint)climbedField.GetValue(_robot)).climberAngle = oldAngle.Value;
        if (oldPrep.HasValue) ((Prefabs.Reefscape.Robots.Mods.SPAM._180.AnemoneSetpoint)prepField.GetValue(_robot)).climberAngle = oldPrep.Value;
        L($"RESULT {_robot.GetType().Name} {side} angle={_angle} {(cs.ScoringTriggered && !dc.IsTouchingGround ? "CLIMBED" : "no")} everLifted={lifted} {State(cs, dc)}");
    }

    IEnumerator Boot(string metaPath)
    {
        var meta = AssetDatabase.LoadAssetAtPath<RobotMetadataSO>(metaPath);
        Assert.NotNull(meta, metaPath);
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
        Assert.NotNull(_robot, "robot not spawned");
        L($"robot {_robot.GetType().Name} from {metaPath}");
        try { _robot.GetComponent<PlayerInput>().SwitchCurrentControlScheme("Robot 1", _kb); } catch (Exception e) { L("scheme " + e.Message); }
        yield return new WaitForSeconds(1f);
        new GameObject("KeepEnabled").AddComponent<AnemoneKeepEnabled>().timer = Object.FindFirstObjectByType<BaseTimerManager>();
        yield return new WaitForSeconds(2f);
    }

    // INTAKE_META = robot metadata asset, INTAKE_VIDEO = output folder: a coral laid across the deployed ground intake,
    // filmed side-on and from above while the robot takes it
    [UnityTest]
    [Timeout(600000)]
    public IEnumerator WatchIntake()
    {
        var metaPath = Environment.GetEnvironmentVariable("INTAKE_META") ?? "Assets/Prefabs/Reefscape/Robots/Mods/SPAM/180/Anemone.asset";
        var video = Environment.GetEnvironmentVariable("INTAKE_VIDEO");
        yield return Boot(metaPath);
        var open = new Vector3(-2.5f, 0, 1.6f);
        PlaceRobot(open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var p0 = _robot.transform.position;
        Hold(Key.W); yield return new WaitForSeconds(0.5f); Release(Key.W); yield return new WaitForSeconds(0.5f);
        _wDir = (_robot.transform.position - p0).normalized;
        PlaceRobot(open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        p0 = _robot.transform.position;
        Hold(Key.D); yield return new WaitForSeconds(0.5f); Release(Key.D); yield return new WaitForSeconds(0.5f);
        _dDir = (_robot.transform.position - p0).normalized;
        PlaceRobot(open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        yield return Tap(Key.K); // robots start holding a coral: score it away
        yield return new WaitForSeconds(1.5f);
        if (_robot.CurrentRobotMode != Games.Reefscape.Enums.ReefscapeRobotMode.Coral) yield return Tap(Key.Semicolon);
        if (_robot.CurrentCoralStationMode.DropType != DropType.Ground) yield return Tap(Key.V);
        yield return Tap(Key.Z);
        yield return new WaitForSeconds(1f);
        Hold(Key.LeftShift);
        yield return new WaitForSeconds(2.5f);
        var intakes = _robot.GetComponentsInChildren<Games.Reefscape.GamePieceSystem.ReefscapeGamePieceIntake>(true);
        L("intakes: " + string.Join(", ", intakes.Select(i => $"{i.name}{_robot.transform.InverseTransformPoint(i.transform.position):F2}")));
        var intake = intakes.Where(i => i.name.ToLower().Contains("coral") || i.name.ToLower().Contains("ground"))
            .OrderBy(i => i.transform.position.y).First();
        var local = _robot.transform.InverseTransformPoint(intake.transform.position);
        var coral = Object.FindObjectsByType<RobotFramework.Controllers.GamePieceSystem.GamePieceController<Games.Reefscape.GamePieceSystem.ReefscapeGamePiece, Games.Reefscape.GamePieceSystem.ReefscapeGamePieceData>>(FindObjectsSortMode.None)
            .Where(p => p.PieceName == "Coral" && !p.transform.IsChildOf(_robot.transform))
            .OrderBy(p => (p.transform.position - intake.transform.position).sqrMagnitude).First();
        Time.captureFramerate = 30;
        var target = new Vector3(0, 0.35f, local.z * 0.7f);
        var cams = new[] { ("side", new Vector3(1, 0.15f, 0)), ("top", new Vector3(0.35f, 1f, Mathf.Sign(local.z) * 0.5f)) }.Select(c =>
        {
            var r = new GameObject("Rec " + c.Item1).AddComponent<AnemoneRecorder>();
            r.Init(Path.Combine(video, c.Item1), _robot.transform, target, c.Item2, 1.5f);
            return r;
        }).ToList();
        yield return new WaitForSeconds(0.3f);
        var rb = coral.GetComponent<Rigidbody>();
        var toIntake = _robot.transform.forward * Mathf.Sign(local.z);
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.position = _robot.transform.position + toIntake * (Mathf.Abs(local.z) + 0.75f) + Vector3.up * 0.06f;
        rb.rotation = Quaternion.FromToRotation(Vector3.forward, _robot.transform.right);
        yield return new WaitForSeconds(0.2f);
        var keys = DriveKeys(toIntake);
        Hold(keys);
        var dc = _robot.GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
        dc.SetDriveMp(0.5f);
        L($"driving {string.Join("+", keys)} at coral, intake {intake.name}{local:F2}, sp={_robot.CurrentSetpoint}");
        var stopped = false;
        for (var i = 0; i < 28; i++)
        {
            yield return new WaitForSeconds(0.25f);
            var gap = Vector3.Dot(coral.transform.position - _robot.transform.position, toIntake);
            if (!stopped && coral.transform.IsChildOf(_robot.transform)) { Release(keys); stopped = true; }
            L($"+{(i + 1) * 0.25f:F2}s sp={_robot.CurrentSetpoint} coral(robot)={_robot.transform.InverseTransformPoint(coral.transform.position):F2} long={_robot.transform.InverseTransformDirection(coral.transform.forward):F2}");
        }
        dc.SetDriveMp(1f);
        foreach (var r in cams) Object.Destroy(r.gameObject);
        Time.captureFramerate = 0;
        Release(Key.LeftShift);
        L("DONE");
    }

    [UnityTest]
    [Timeout(1200000)]
    public IEnumerator Watch()
    {
        var metaPath = Environment.GetEnvironmentVariable("CLIMB_META") ?? "Assets/Prefabs/Reefscape/Robots/Mods/SPAM/180/Anemone.asset";
        var sides = (Environment.GetEnvironmentVariable("CLIMB_SIDES") ?? "front,back,left,right").Split(',');
        yield return Boot(metaPath);
        _cam = new GameObject("WatchCam").AddComponent<Camera>();
        _cam.fieldOfView = 40;
        _cam.enabled = false;
        _rt = new RenderTexture(960, 720, 24);
        _cam.targetTexture = _rt;

        // drive probe
        var open = new Vector3(-2.5f, 0, 1.6f);
        PlaceRobot(open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        var p0 = _robot.transform.position;
        Hold(Key.W); yield return new WaitForSeconds(0.5f); Release(Key.W); yield return new WaitForSeconds(0.5f);
        _wDir = (_robot.transform.position - p0).normalized;
        PlaceRobot(open, Vector3.forward);
        yield return new WaitForSeconds(0.5f);
        p0 = _robot.transform.position;
        Hold(Key.D); yield return new WaitForSeconds(0.5f); Release(Key.D); yield return new WaitForSeconds(0.5f);
        _dDir = (_robot.transform.position - p0).normalized;

        var cage = GameObject.FindGameObjectsWithTag("Cage").Select(c => c.GetComponent<Collider>()).Where(c => c && c.attachedRigidbody)
            .OrderBy(c => Mathf.Abs(c.transform.position.z)).First().attachedRigidbody;
        // CLIMB_ANGLES (Anemone): climbed angles to try, one run each per side
        var angles = (Environment.GetEnvironmentVariable("CLIMB_ANGLES") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => (float?)float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(null).ToList();
        foreach (var angle in angles)
        foreach (var side in sides)
        {
            _angle = angle;
            yield return Try(side.Trim(), cage);
        }
        L("DONE");
    }
}
#endif
