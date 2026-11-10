# Making a Robot Mod (MoSim, 2025 Reefscape)

This guide takes a simplified CAD model to a robot that drives, intakes and scores coral and algae, climbs, and looks like the real one. You check each part of it against real footage.

It assumes the model is already simplified: a few hundred thousand triangles, parts named, and motors and wheels taken from the library. If it isn't, run the simplification skill first: **[model-optimization-skill](https://github.com/MoSim-Modding-Fun/model-optimization-skill)**. Its `SKILL.md` has the workflow.

Every class, field, menu and key name below was checked against this repo's code. If one doesn't match what you see, trust the code and fix this guide.

---

## 0. Ground rules

These come from earlier mods (341, 971, 180, 3173, 5026, 1114). Each one was learned the hard way.

| Rule | Why |
|---|---|
| **No builder scripts.** Don't keep a `XBuilder.cs` that generates or rewrites the robot. The prefab and asset YAML hold everything. After the first build in the editor, change things in the YAML: use `Tools/unityscan.py set`, or careful edits for structure. Older mods (`Assets/Editor/MissDaisyBuilder.cs`, `AnemoneBuilder.cs`) were made with builders; don't copy that. | A rebuild wipes hand edits, and nobody can see what the robot really is. |
| **No mesh colliders.** Build shapes from boxes and capsules. | Performance and stable contacts. |
| **No magic physics.** No helper forces, snaps, teleports, invisible pushes, spawned joints or pull-force "locks". A hook holds by its collider shape. An intake pulls a piece only once the piece touches its rollers. | The game must play like the real robot. |
| **Automatic inertia tensor** on every jointed Rigidbody. Only the climber joint turns it off, using tensor (1,1,1) and mass 2. | Hand tensors caused wobble and explosions. |
| **Don't edit the framework or shared field assets for a mod** (`DriveController`, `RobotBase`, `GamePieceIntake`, Coral and Algae prefabs, the Reefscape scene). Fix it on the mod. | Changes there affect every robot. |
| **Drive values and setpoints come from the team's public code.** Use footage for the motion and timing, and as a cross-check. If a mod is already mostly done, keep its hand-tuned setpoints and only fix ones that are broken or placeholders. | Realism you can defend, without undoing someone's tuning. |
| **Test with real intake.** Pick coral up off the floor or from the coral station, and algae off the reef, by driving the robot. Never spawn a piece into the robot. The preload is the only exception. | Spawned pieces hide intake and handoff problems. |
| **Never work in the main `MoSim-Reefscape-Public` folder.** Make your own reflink copy, edit and test there, and copy finished changes back only while the owner isn't in Play mode (section 7). | The owner playtests in the main folder, and any edit there makes their editor reimport. |
| **Commits** only when the owner asks: your own paths, on the branch they name. No Co-Authored-By or "Generated with" lines. | Many sessions share the repo. |

---

## 1. Gather the inputs

1. **Simplified model.** Export it as `.fbx`, and set its import **Normals** to **Import**. Note the up axis; `-Y`-up GLBs need a rotated root.
2. **Team code.** Find the team's public 2025 repo (usually GitHub, e.g. `Team341/FRC2025-Public`). From it you need:
   - **Swerve:** top speed (`kSpeedAt12Volts` / `MaxSpeed`), drive gear ratio, wheel diameter, robot mass (from the PathPlanner settings).
   - **Setpoints:** elevator heights, arm and wrist angles, intake angles for stow, intake, L1–L4, low/high algae, processor, barge and climb.
   - **Sequencing:** what moves first (e.g. elevator up before the arm swings), roller speeds, the climb steps.
3. **Footage.** Use the latest event the robot played (the Championship division and Einstein for top teams). Get match videos from the team's The Blue Alliance page; `yt-dlp` is fine. Behind the Bumpers and Pit Stop videos explain mechanisms in the team's own words. Store videos outside `/tmp`, which is a RAM disk.
4. **Photos.** Close-ups from TBA media and the team's socials, for colours, decals, LEDs and bumpers. CAD colours are often wrong.
5. **Team logo.** Take it from the team's own site or socials, as a transparent PNG.

---

## 2. Files and where they go

```
Assets/Prefabs/Reefscape/Robots/Mods/<Pack>/
  <Pack>.asset                 ReefscapeBaseModpackSo   (Create > Games/Reefscape/ReefscapeModpackMetadata)
  <Team>/
    <Name>.asmdef              the mod's script assembly (its DLL ships with the mod)
    <Name>.cs                  robot script, subclass of ReefscapeRobotBase
    <Name>Setpoint.cs          setpoint ScriptableObject
    Setpoints/*.asset          one asset per setpoint (Stow, Intake, L1..L4, LowAlgae, HighAlgae, Processor, Barge, Climb...)
    <Name>.asset               ReefscapeRobotMetadataSO (Create > Games/Reefscape/ReefscapeRobotMetadata)
    <Team>.prefab              the playable robot
    MainMenu<Team>.prefab      menu copy: same visuals, no scripts/joints/intakes
    <model>.fbx                simplified model (or in Models/)
    Bumper Materials/          Blue Bumpers.mat, Red Bumpers.mat
    TutorialCards/             TutorialCard*.prefab + their images
    <Team>Logo.png             Sprite import, transparent background
```

**The robot metadata asset (`<Name>.asset`):** set these fields:
- `teamNumber` and `teamName`
- `robotPrefab` and `mainMenuPrefab`
- `robotPreloadGamePiece`, the coral it starts with
- `robotImage`, the logo
- `tutorialCards`, the robot's own cards; don't reuse another robot's
- `autoClimbs`, if the robot climbs automatically

**The modpack asset:** fill `modpackName`, `modpackVersion`, `authorName` and `description`, and add the metadata to its `robots` list.

**The assembly:** copy the `.asmdef` from a finished mod (e.g. `Mods/LynkModOfficial/9496`). It references Reefscape, RobotFramework, MoSimCore, MoSimLib, GameSystems and the Input System. Use one namespace per mod, e.g. `Prefabs.Reefscape.Robots.Mods.<Pack>._<Team>`. A `.cs` added outside the editor isn't compiled until Unity has imported it; check with `python Tools/unitybuild.py`.

**Addressables (shipping):** each pack is an Addressables group whose entry is the pack folder, with the pack asset labelled `modpack_metadata` (see `Assets/AddressableAssetsData/AssetGroups/`). **The group name must match the pack folder name exactly.** Otherwise the exporter silently skips the mod's DLL.

---

## 3. Prefab structure

Build it once in the editor from a clean, finished mod. Use `Mods/LynkModOfficial/9496` (the docs' walkthrough robot) or a recently finished robot, and never a mod someone is still working on. Strip everything the template robot added. Then change it through the YAML (section 0).

```
<Team>   tag BlueRobot, layer Robot
         [Rigidbody, DriveController, <Name> script, ReefscapeRobotGamePieceController, GenericElevator,
          ReefscapeAutoAlign, ClimbScorer, BoxCollider (trigger), PlayerInput, LedStripController]
  Drivetrain            prefab instance of Assets/Prefabs/Reefscape/Robots/Drivetrain.prefab
  Stationary / Model    fixed parts of the FBX, with box/capsule colliders grouped by part
  Elevator
    Stationary          base tubes
    Stage One           [Rigidbody, ConfigurableJoint (Y motion free), GenericJoint]
      Stage Two         same; the carriage
        Arm             [Rigidbody, ConfigurableJoint (angular X free), GenericJoint]
          Wrist         same; holds the claw, its intakes and the piece targets
  Ground Intake / Pivot [Rigidbody, ConfigurableJoint, GenericJoint]   rollers are children
  Climber / Pivot       [Rigidbody, ConfigurableJoint, GenericJoint]   auto tensor off, (1,1,1), mass 2
  Bumpers               meshes using the bumper materials, plus team numbers on every side
```

- **Root Rigidbody:** mass 98, drag 3, angular drag 5, like the base robots (2910).
- **PlayerInput:**
  - actions: `Assets/Controls/Reefscape.inputactions`
  - default map: `Robot`
  - behaviour: Invoke Unity Events
- **Orientation:** turn the model so the scoring side is the robot's front **before** you make any joints. Rotating finished mechanisms leaves a yaw between the joints and the root, and `GetSingleAxisAngle` breaks. Never rotate the shared Drivetrain, because its module frames set the drive directions.
- **Start inside the frame:** rest every mechanism in its real stowed pose, with the joints reading 0 there. For a linkage, turn each link's children about its pivot and move only the joint objects.
- **Moving parts:** each part is a child Rigidbody with a `ConfigurableJoint` (one axis free) connected to its parent's body, plus a `GenericJoint`.
  - **Masses:** use realistic masses. Stages at 1 kg against a 98 kg body give way under load.
  - **Self-contact:** joint bodies set `excludeLayers = Robot` so the robot's own parts don't touch.
- **Four-bars:** put one `GenericJoint` on the driven link, and close the loop with passive `HingeJoint`s. Don't put a sprung, limited hinge on the driven link, because it flips the loop.
- **Arm and wrist wobble:** if a hit twists the arm off its hinge, raise `JointStackStabilizer.solverIterations` and `solverVelocityIterations` from the robot's `Start`. 971 uses 200 and 75. `RobotBase` adds the stabilizer itself.

### Colliders

- **Shape:** follow each part with boxes and capsules. Give thin plates a 10–15 mm skin, and fill whole panels. Parts that sit outboard of a piece's path must block it, like the real ones do.
- **`excludeLayers` on a collider overrides the body's.** A collider with its own `excludeLayers` and `layerOverridePriority` must still include `Robot`, e.g. `GetMask("Coral", "Robot")`. Otherwise it collides with the robot's other bodies and jams linkages.
- **Reef branches are on the Field layer, not Default.** Excluding Default from a held coral doesn't stop it pressing on a branch.
- **Clearances:** keep deployed intakes off the carpet at rest.

### Intakes, game pieces, rollers

- **Rollers:** a `GenericRoller` on a `Rigidbody` + `HingeJoint` with the motor on. Its speed is the HingeJoint motor's `targetVelocity`, and its `CapsuleCollider` uses the `Grip` physics material. Visual-only wheels use `GenericAnimationJoint`.
- **Intake** (`ReefscapeGamePieceIntake`): a thin trigger box on the roller face where the piece touches, never a large catch box out in front. The fields are:

  | Field | What it is |
  |---|---|
  | `intakeCollider` | the trigger boxes |
  | `intakeSpeed` | in/s |
  | `intakeForce` | |
  | `usePlanarTolerancing` | planar mode instead of distance mode |
  | `maxDistance` | |
  | `accuracy` | |
  | `lockXAxis`, `lockYAxis`, `lockZAxis` | lock motion on an axis |
  | `useRotaion` | spelled that way in the code |
  | `smoothHandoff` | |
  | `target` | where the piece goes |

  - **Keep the box centre at 0.** `OverlapBox` uses the collider's transform and ignores `BoxCollider.center`, and the same applies to `ClimbScorer` triggers.
  - **Keep `target` within `maxDistance` of the intake.** On 971 the ground intake's target sat on the wrist, too far away, so the coral was picked up and dropped every frame.
- **States** (`GamePieceState`): set `name`, `stateNum`, `stateMoveSpeed`, `stateTarget`, `smoothHandoff` and `angularSpeed` on each.
  - `stateNum` must be unique and above 0.
  - If you use `SetTargetState(nameof(x))`, `name` must match the variable name.
  - Each state change can jump the piece a whole segment, so prefer one intake target plus one held state over long waypoint chains.
  - When a piece turns while it moves, give each leg the turn rate that finishes on arrival.
- **Piece paths:** pull pieces in and up like the real robot does. Turn them only where the real mechanism turns them.

---

## 4. Drivetrain

Set these on the root `DriveController`:

| Field | Value |
|---|---|
| `wheelDiameter` | inches, from the team code |
| `maxSpeed` | **feet per second** (`SecureFloat`): the code's top speed in m/s ÷ 0.3048, unscaled. The 2026 guide's 0.95 × rule doesn't apply to 2025 robots. |
| `accelerationForce` | `SecureFloat`; base robots use 8 |
| `falloffPercent`, `falloffExponent`, `steerMultiplier` | keep the base robots' values (0.075, 10, 1) unless footage says otherwise |

- **Editing a `SecureFloat` in YAML:** its bits are XORed with `_obfuscationKey`. Keep the key and write `_encryptedValue = bits(new value) ^ key`. `unityscan.py scripts` shows the decoded value.
- **Never** add your own turn scaling or drive logic in the robot script.
- **Check:** at full input, compare top speed and turn rate with footage and with the base robots.

---

## 5. Robot script and controls

```csharp
public class MyRobot : ReefscapeRobotBase
{
    [SerializeField] private GenericElevator elevator;
    [SerializeField] private GenericJoint arm, wrist, intakePivot, climber;
    [SerializeField] private PidConstants armPid, wristPid, intakePid, climberPid;
    [SerializeField] private MySetpoint stow, intake, l1, l2, l3, l4, lowAlgae, highAlgae, processor, barge, climb, climbed;
    [SerializeField] private ReefscapeGamePieceIntake groundIntake, clawCoralIntake, clawAlgaeIntake;
    [SerializeField] private GamePieceState coralHeld, algaeHeld;

    private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coral, _algae;

    protected override void Start()
    {
        base.Start();
        arm.SetPid(armPid); wrist.SetPid(wristPid); intakePivot.SetPid(intakePid); climber.SetPid(climberPid);
        _coral = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
        _algae = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));
        _coral.gamePieceStates = new[] { coralHeld };
        _coral.intakes.Add(groundIntake); _coral.intakes.Add(clawCoralIntake);
        _algae.gamePieceStates = new[] { algaeHeld };
        _algae.intakes.Add(clawAlgaeIntake);
        RobotGamePieceController.SetPreload(coralHeld);
    }

    private void LateUpdate() { arm.UpdatePid(armPid); /* ...each joint */ }

    private void FixedUpdate()
    {
        _coral.RequestIntake(groundIntake, IntakeAction.IsPressed() && !_coral.HasPiece());
        _coral.SetTargetState(coralHeld);
        switch (CurrentSetpoint)
        {
            case ReefscapeSetpoints.Stow:  Apply(stow); break;
            case ReefscapeSetpoints.L4:    Apply(l4); break;
            case ReefscapeSetpoints.Place: Place(); break;
            // Intake, L1..L3, Stack, LowAlgae, HighAlgae, Processor, Barge, RobotSpecial, Climb, Climbed
        }
    }

    private void Apply(MySetpoint s)
    {
        elevator.SetTarget(s.elevatorHeight * 39.37f);                 // GenericElevator takes inches
        arm.SetTargetAngle(s.armAngle).withAxis(JointAxis.X).noWrap(180).useCustomStartingOffset(0);
        // wrist, intake pivot, climber the same way
    }

    private void Place()
    {
        if (_algae.HasPiece()) _algae.ReleaseGamePieceWithForce(new Vector3(0, 0, 4));
        else _coral.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 3), 0.5f, 0.5f);
    }
}
```

**Building it:**
- **PID:** a `GenericJoint` does nothing until `SetPid` is called. `UpdatePid` in `LateUpdate` lets you tune live in the inspector.
- **Intake requests:** `RequestIntake` stays on until you pass `false`, and the intake **drops a held piece the moment its request goes false**. Gate it on the button, not on fragile "at setpoint" checks.
- **The right node:** never pass an algae intake to the coral node's `RequestIntake`, because it relabels the intake.
- **Releasing:** a release with zero force doesn't score. For each level, start from the team's roller speeds.
- **Holding a joint:** hold the last command, not the measured position. A hold that follows the measured value ratchets under gravity.

**Controls** (`Assets/Controls/Reefscape.inputactions`, map `Robot`). `ReefscapeRobotBase.Update` turns buttons into `CurrentSetpoint` (enum `ReefscapeSetpoints`):

| Key | Action | Sets |
|---|---|---|
| W A S D / J L | Translate / Rotate | drive |
| Shift | Intake | `Intake` while held. In `LowAlgae`, `HighAlgae` or `Stack` it stays put and the robot grabs algae. |
| K | Place (`OuttakeAction`) | `Place`. `LastSetpoint` tells you what to score. |
| U | L1 | holding coral → `L1`; holding algae → `Processor`; empty: `Stow`, or `Stack` from Stow in algae mode |
| I | L2 | holding coral → `L2`; empty → `LowAlgae`; press again → `Stow` |
| O | L3 | holding coral → `L3`; empty → `HighAlgae`; press again → `Stow` |
| P | L4 | holding coral → `L4`; holding algae → `Barge`; press again → `Stow` |
| Z | Stow | `Stow` (not during a climb) |
| C | Climb | Stow → `Climb` → `Climbed` → Stow |
| ; | RobotModeToggle | coral/algae mode (`CurrentRobotMode`) |
| ' | IntakeModeToggle | normal/L1 intake (`CurrentIntakeMode`) when holding no coral |
| V | RobotSpecial | toggles `RobotSpecial` |
| Q / E | AutoAlignLeft / Right | `ReefscapeAutoAlign` to the left/right branch |

Each robot decides what its setpoints do. Report any button use beyond this table to the owner.

---

## 6. Matching real footage

Measure the real robot, then make the sim match within a few percent. Every action needs at least one real clip compared side by side with the sim.

| Measure | From footage | In the sim |
|---|---|---|
| Top speed, turn rate | frame-count across known field distances | full input on open floor |
| Cycle time | time between reef visits, from contact sheets with the match clock burned in | a scripted station → reef cycle |
| Coral intake | ground and/or station, time from contact to held | drive onto a floor coral / take a station drop |
| Scoring per level | how the coral leaves and lands on L1–L4 | place at each level on a reef **with its algae removed** |
| Algae | grab off the reef (low/high), processor, barge throw | the same, checking the piece stays scored |
| Climb | time, how it latches, the final hang pose | deep or shallow cage from the usual approach |
| Auto | the team's routine and score | optional scripted auto |

- **Contact sheets:** a single `ffmpeg` command crops to the robot's side of the field and tiles frames with the match clock burned in.

  ```bash
  ffmpeg -ss <start> -i x.mp4 -vf "fps=1/2,crop=...,scale=480:-1,drawtext=...,tile=4x4" out.png
  ```

  You can read the frames even if you can't play the video.
- **Team code can be stale:** compare it with what they ran at the event. For example, 1114's funnel took about 3 s on the field, not the 12 s its code implied.
- **Edge cases worth a test each:** full-speed driving into a wall (arm and wrist wobble), driving over loose coral and algae, scoring while pushed.

**Scoring traps that cause false results:**
- **Score snapshot:** `ReefscapeScoreHandler`'s score data is one object, refilled every `LateUpdate`. Snapshot it (copy it) before comparing before and after.
- **Algae block levels:** low algae blocks L2 and high algae blocks L3, like the real game. A repeat miss at L2 or L3 is usually algae, not aim.
- **Processor:** algae pushed gently into the opening rolls down an internal channel and scores. A hard eject bounces it back out.
- **Barge:** `BargeScorer` counts any algae in its box, including one still held, so check that the piece stays after the robot backs away.
- **Deep cage climbs:** approach slowly with the arm lined up between two uprights, ease forward until an upright leans on the arm, then climb. Never ram the cage, because the bumper rides up onto it.
- **Coral orientation:** spawned and preplaced coral prefabs have differently rotated mesh children. Get each piece's long axis through its mesh transform.

---

## 7. Running tests and iterating (one computer)

Every number in section 6 comes from a scripted PlayMode test, not from playing by hand. A test is the same run every time: same start, same inputs, same timestep.

### 7.0 Your own copy

```bash
cd /home/seb/Desktop/MoSimulator
cp -a --reflink=always MoSim-Reefscape-Public MoSim-Reefscape-Work-<team>
rm -rf MoSim-Reefscape-Work-<team>/Temp
```

- **The copy:** do every edit and every Unity run in this copy. On btrfs it's almost free, and `Library` comes along, so there's no reimport.
- **Disk:** delete the copy when you're done; the disk is nearly full.
- **Copying back to main:** copy finished, verified changes into the main folder only while the owner isn't in Play mode. Check that no editor has `-projectpath .../MoSim-Reefscape-Public`, or that `~/.config/unity3d/Editor.log` isn't growing with runtime lines. If you can't tell, wait.

### 7.1 The test

Tests use the Unity Test Framework. Put them in `Assets/Tests/<Name>PlayTests/`, with an `.asmdef` like `Assets/Tests/MissDaisyPlayTests/MissDaisyPlayTests.asmdef`:
- references: `UnityEngine.TestRunner`, `Unity.InputSystem`, `Unity.Addressables`, `Reefscape`, `RobotFramework`, `MoSimCore`, `MoSimLib`, `GameSystems` and your mod's assembly
- `nunit.framework.dll`
- define constraint `UNITY_INCLUDE_TESTS`

Keep tests in your copy and don't commit them. `MissDaisyPlayTest.cs` is the full working example.

```csharp
public class MyRobotPlayTest
{
    Keyboard _kb; readonly List<Key> _keys = new();
    void Hold(Key k, bool down) { if (down) { if (!_keys.Contains(k)) _keys.Add(k); } else _keys.Remove(k);
                                  InputSystem.QueueStateEvent(_kb, new KeyboardState(_keys.ToArray())); }

    [UnityTest]
    public IEnumerator Scoring()
    {
        Time.captureDeltaTime = 1f / 60f;                                   // fixed steps: repeatable runs
        _kb = InputSystem.AddDevice<Keyboard>();
        var meta = AssetDatabase.LoadAssetAtPath<ReefscapeRobotMetadataSO>("Assets/Prefabs/Reefscape/Robots/Mods/<Pack>/<Team>/<Name>.asset");
        SceneManager.sceneLoaded += (_, _) => {                             // pick the robot before it spawns
            var rsc = Object.FindFirstObjectByType<RobotSpawnController>();
            typeof(RobotSpawnController).GetField("robotOverride", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(rsc, meta); };
        yield return EditorSceneManager.LoadSceneAsyncInPlayMode("Assets/Scenes/Reefscape.unity", new LoadSceneParameters(LoadSceneMode.Single));
        ReefscapeRobotBase robot = null;
        while (robot == null || BaseGameManager.Instance.RobotState != RobotState.Enabled)
        { robot = Object.FindFirstObjectByType<ReefscapeRobotBase>(); yield return null; }
        Object.FindFirstObjectByType<BaseTimerManager>().PauseTimer();      // stop the match clock
        robot.transform.SetPositionAndRotation(new Vector3(...), Quaternion.Euler(0, 180, 0));  // same start every time
        Physics.SyncTransforms();
        yield return new WaitForSeconds(1.5f);

        Hold(Key.P, true); yield return null; Hold(Key.P, false);           // L4
        yield return new WaitForSeconds(2f);
        Hold(Key.K, true); yield return null; Hold(Key.K, false);           // Place
        yield return new WaitForSeconds(1f);
        Debug.Log($"RESULT L4 released={!RobotHasCoral(robot)}");          // one RESULT line per measurement
    }
}
```

- **Piece states:** read held pieces from `ReefscapeRobotGamePieceController.GetPieceByName("Coral").HasPiece()`.
- **Score:** read it from `ReefscapeScoreHandler`, and snapshot it (section 6).
- **Logging:** log contacts (`OnCollisionStay` on the robot's bodies), joint angles and positions. You want enough to see a cause, not just a pass or fail.

### 7.2 Running it

**Editor open:** in your copy, open **Window > General > Test Runner > PlayMode** and run a test. You can watch it in the Game view.

**Headless:** only one Unity can open a project, so headless runs go on a copy too.

```bash
/home/seb/Desktop/MoSimulator/unity_pick_host.sh          # last line: server, pc or wait
xvfb-run -a /home/seb/Unity/Hub/Editor/2023.2.22f1/Editor/Unity -batchmode -projectPath <copy> \
  -runTests -testPlatform PlayMode -testFilter 'MyRobotPlayTest.Scoring' \
  -testResults <copy>/Logs/results.xml -logFile <copy>/Logs/test.log
grep RESULT <copy>/Logs/test.log
```

**On the PC:**
- Run Unity under a memory cap of at least 6 GB, using the guard or `systemd-run --user --scope -p MemoryMax=6G`. A 4 GB cap gets killed.
- Anything uncapped runs inside the Claude app's own memory, and has taken the whole app down.

**On the server (`seb@192.168.1.217`):**
- It has the same Unity path and its own project copies.
- Follow `unity_pick_host.sh` and quit Unity after every run.

**Gotchas we hit:**
- **Failed builds look like passes:** a batch `-executeMethod` exits 0 even when the method threw. Check the log's success line and the asset's timestamp before trusting a run.
- **Scripts not recompiled:** after copying files into a project with their times kept, `touch` the changed `.cs` and assets. Otherwise Unity skips the recompile and scripts show as missing.
- **One compile error breaks everyone's runs:** any `.cs` in `Assets` is compiled by every Unity run on that project. Stage new scripts outside `Assets`, or work in your own copy.
- **Long suites grow past 6 GB:** split them into shorter runs with Unity restarted between them, or run them on the server.
- **Segfault at shutdown:** Unity sometimes segfaults while quitting, in its audio (FMOD) thread. Check that the RESULT lines were written before treating it as a failure. `-nosound` removes that thread on the server.
- **`/tmp` is a RAM disk:** frames and videos there count as memory. Keep them elsewhere and delete them when you're done.
- **New `[SerializeField]` defaults only reach the prefab when you set them in the YAML.** Tests read the serialized values.

### 7.3 The iteration loop

1. **Measure first.** Run the tests on the robot as it is and save the numbers. That's your baseline.
2. **Compare with footage** (section 6) and pick the single biggest gap.
3. **Find the cause, not a fudge.** Log enough to see it. A wrong number usually has a physical reason: a collider gap, a wrong mass, the intake target too far away, algae blocking a level, a part catching the carpet.
4. **Try candidate values for one run only,** by setting the field from the test, before you touch the prefab.
5. **Change one thing in the YAML:** `python Tools/unityscan.py set <prefab> field=value --name <Script> --write`. Close the asset in Unity first, or the editor writes its own copy back. Add `--check-overrides` if a scene might override it.
6. **Rerun the same test** and compare with the baseline in a small table. Repeat noisy tests 3–5 times.
7. **Check nothing else broke.** After any physics change, rerun the full set: drive, intakes, every level, algae, climb, wall crash.
8. **Keep a short log** per robot: date, what changed, the before → after numbers, and why.

When the numbers match footage and section 9 passes, play it yourself. Feel is part of matching the real robot.

---

## 8. Colour and look

- **Colours:** take them from photos and footage, not CAD. Map each part to the shared materials in `Assets/Materials`:
  - metal and plastic: `Aluminum`, `DarkSteel`, `LightBlack` (motors), `Yellow`, `Red`
  - clear parts: `Polycarb` / `SmokedPolycarb`
  - wheels and treads: `GreenFlexWheel` / `BlackFlexWheel`
  - colours the shared set doesn't have: copy them into the mod's own folder, so the bundle carries them
- **Bearings** are one colour. Add Kraken caps only where the CAD has them.
- **Transparency:** only for real polycarbonate. Basically opaque panels use opaque materials.
- **Bumpers:**
  - Blue and red materials go in the mod's `Bumper Materials/` folder and are assigned on the robot script's bumper fields.
  - Team numbers go on every side; white numbers sit on the bumper's material slot 1.
- **LEDs:** copy the real robot's patterns from footage, using the `LedStripController` on the root.
- **Sounds:**
  - roller loops, algae stall, piece seating and hook clicks
  - joint motors, from `Assets/Imports/Audio`
- **Logo and cards:** the metadata `robotImage` is a sprite with a transparent background. The tutorial cards are the robot's own.

---

## 9. Checklist before shipping

- [ ] `python Tools/unitybuild.py`: 0 errors. `python Tools/unitylog.py --new`: no new exceptions.
- [ ] `python Tools/unityscan.py doctor`: no broken references in the robot's prefabs.
- [ ] No MeshColliders; automatic inertia tensor everywhere except the climber; no builder or test scripts in the mod.
- [ ] Spawns from the menu, and the menu model looks right.
- [ ] Drive matches the code and footage, with no wobble or tipping in a full-speed crash.
- [ ] Ground and/or station coral intake works by contact only. The handoff holds through stow, with no piece passing through a part.
- [ ] L1–L4 score from both alignment sides (Q/E); low and high algae grabs; processor and barge score and stay scored.
- [ ] The climb latches and scores from the usual approach.
- [ ] Controls as in section 5. Any extra button is reported to the owner.
- [ ] Colours, bumpers, numbers, LEDs, sounds, logo and tutorial cards checked in-game against photos.
- [ ] The Addressables group is named exactly like the pack folder, and `Tools > Addressables > Build And Export Mods` (or `Tools/build-mods-all-platforms.sh --groups "<Pack>"`) builds zips for every platform without errors.
- [ ] Changes copied into the main folder only while the owner wasn't in Play mode; committed only when they ask.

---

## 10. Tool reference

| Task | Command |
|---|---|
| Compile | `python Tools/unitybuild.py [--warnings]` |
| Errors from the editor log | `python Tools/unitylog.py [--new\|--player\|--tail N\|--grep RE]` |
| Prefab tree / find / fields | `python Tools/unityscan.py tree <asset> [--depth N]`, `find "<regex>"`, `scripts <asset> --name <Script>`, `info`, `usage <script>`, `refs`, `deps` |
| Change a serialized field | `python Tools/unityscan.py set <asset> field=value --name <Script> [--on <path regex>] --write` (close the asset in Unity first) |
| Broken references | `python Tools/unityscan.py doctor` |
| Pick a machine for a Unity run | `/home/seb/Desktop/MoSimulator/unity_pick_host.sh` |
| Headless PlayMode tests | `Unity -batchmode -projectPath <copy> -runTests -testPlatform PlayMode -testFilter <Class.Method> -testResults <xml> -logFile <log>` |
| Build mod zips | `Tools > Addressables > Build And Export Mods` (editor), or `Tools/build-mods-all-platforms.sh --groups "<Pack>"` (close Unity first) |
| Model simplification | [model-optimization-skill](https://github.com/MoSim-Modding-Fun/model-optimization-skill) |
