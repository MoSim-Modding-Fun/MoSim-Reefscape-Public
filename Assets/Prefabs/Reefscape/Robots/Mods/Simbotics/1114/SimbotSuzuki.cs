using System.Linq;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using RobotFramework.Components;
using RobotFramework.Controllers.Drivetrain;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Simbotics._1114
{
    /// <summary>
    /// 1114 Simbotics' 2025 robot, Simbot Suzuki: a two-stage elevator, an arm that swings through the carriage to take
    /// station coral onto a roller tongue from a big rear funnel, a hoop at the top of the arm for algae, and a side-entry
    /// deep climber. Setpoints, interlocks and motion limits come from their code (Simbotics/2025-Simbot-Suzuki).
    /// </summary>
    public class SimbotSuzuki : ReefscapeRobotBase
    {
        [Header("Mechanisms")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint arm, funnelLink, climber;

        [Header("PIDs")]
        [SerializeField] private PidConstants armPid;
        [SerializeField] private PidConstants funnelPid, climberPid;

        [Header("Setpoints")]
        [SerializeField] private SimbotSetpoint intakeCoral;
        [SerializeField] private SimbotSetpoint transport, l1, l2, l3, l4, lowAlgae, highAlgae, barge, processor, floorAlgae, stagedAlgae;

        [Header("Game pieces")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
        [SerializeField] private GamePieceState coralState, algaeState;
        [Tooltip("The tongue's axis: a coral leaves along this transform's forward.")]
        [SerializeField] private Transform coralEject;
        [Tooltip("The way the hoop's wheels throw an algae.")]
        [SerializeField] private Transform algaeEject;
        [Tooltip("Colliders a coral passes while the funnel's wheels feed it onto the tongue.")]
        [SerializeField] private Collider[] funnelColliders;

        [Header("Eject speeds, m/s")]
        [SerializeField] private float l1EjectSpeed = 1.2f;
        [SerializeField] private float l2EjectSpeed = 1.2f, l3EjectSpeed = 1.2f, l4EjectSpeed = 0.9f;
        [Tooltip("L1: the wheels' push as the tail leaves tips the coral forward over the trough's lip (m/s at its centre).")]
        [SerializeField] private float l1ForwardKick = 1.2f;
        [SerializeField] private float l1Tumble = 6f;
        [SerializeField] private float bargeEjectSpeed = 2.75f, processorEjectSpeed = 1.8f, algaeDropSpeed = 1f;
        [Tooltip("Reef-side reach: inches added to the auto align's offset for each coral level and for algae.")]
        [SerializeField] private float l1Align, l2Align, l3Align, l4Align, algaeAlign;
        [Tooltip("Inches the auto align stands back while the lift goes up to L3 or L4.")]
        [SerializeField] private float approachStandOff = 7f;

        [Header("Climber")]
        [Tooltip("The head's angle out from upright when the winch's slack is out (deg).")]
        [SerializeField] private float headOutAngle = 95f;
        [Tooltip("Winch: 115 motor rotations through 32.32:1 winds the head back up; deg/s of head.")]
        [SerializeField] private float winchRate = 45f;
        [SerializeField] private Collider[] cageLatches;
        [SerializeField] private BoxCollider cageSeat;
        [SerializeField] private GenericAnimationJoint[] cageWheels;
        [SerializeField] private Transform[] latchVisuals;

        [Header("Funnel four-bar (m)")]
        [Tooltip("Their linear servos: 0 to 95 mm pushes the lower link's gusset back and the knee breaks. Behind the Bumpers: 63:1, about 3 s (their code's 8 mm/s is stale).")]
        [SerializeField] private float servoSpeed = 0.032f;
        [SerializeField] private float servoStroke = 0.095f;
        [Tooltip("Lower link's pivot to its gusset pin (r), and to the servo's fixed pin (d); pin to pin at 0 mm; gusset angle at 0 mm from the line to the fixed pin.")]
        [SerializeField] private float gussetRadius = 0.1136f, fixedPinDistance = 0.182f, servoLengthAtZero = 0.173f;
        [SerializeField] private Transform servoBody;
        [SerializeField] private Transform servoRod, servoFixedPin, servoGussetPin, servoBody2, servoRod2, servoFixedPin2, servoGussetPin2;

        [Header("Rollers (visual)")]
        [SerializeField] private GenericAnimationJoint[] tongueWheels;
        [SerializeField] private GenericAnimationJoint[] algaeWheels, funnelWheels;
        [SerializeField] private float rollerVisualSpeed = 1400f;

        [Header("Drive")]
        [SerializeField] private float topSpeedDrag = 1.35f;

        [Header("Sounds")]
        [SerializeField] private AudioClip rollerClip;
        [SerializeField] private AudioClip algaeStallClip, coralSeatClip, cageLatchClip, servoClip;

        // Real inches per unit of their code: the low to high algae poses are 19.6 units apart and those algae 15.9 in.
        public const float CodeInch = 0.78f;
        private const float InchesPerMeter = 39.3700787f;
        private const float TongueCarry = 0.24f; // m: the wheels push until its tail is off the tongue

        // Their ArmConstants and ElevatorConstants, for the interlocks.
        private const float ArmStart = 89.5f, ArmSafeL3 = 68f, ArmSafeMidFront = 49f, ArmSafeHighAlgae = 35f, ArmSafeLowFront = -30f;
        private const float ElevatorHighSafe = 42f, ElevatorLowSafe = 34.5f, ElevatorIntakeSafe = 1.5f, ElevatorL2Safe = 8f;
        private const float ArmReefClear = -10f, ReefClearance = 1.5f; // deg; m from the reef's centre to the robot's
        private const float DriveSlowHeight = 25f; // "slow to prevent us tipping": 25% drive, 50% turn above this

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coral, _algae;
        private SimbotSetpoint _target;
        private ReefscapeAutoAlign _align;
        private Vector3 _alignOffset;
        private DriveController _drive;
        private float _driveTurn = 1f, _driveScale = 1f;
        private Rigidbody _robotBody;
        private ClimbScorer _climbScorer;

        private bool _released;
        private float _releasedAt, _placeAskedAt = -1f;
        private const float MaxPlaceWait = 1f;
        private ReefscapeSetpoints _placeFrom;
        private GameObject _feeding;
        private float _servo, _head, _elevatorCmd, _armCmd;
        private bool _latched, _hadCoral, _wasLatched;
        private AudioSource _rollerAudio, _stallAudio, _servoAudio, _oneShot;
        private Collider[] _robotColliders;
        private GameObject _algaePassing;
        private Transform[] _reefs = System.Array.Empty<Transform>();
        private Collider[] _armColliders;

        protected override void Start()
        {
            base.Start();

            arm.SetPid(armPid);
            funnelLink.SetPid(funnelPid);
            climber.SetPid(climberPid);

            _target = transport;
            _armCmd = ArmStart;
            RobotGamePieceController.SetPreload(coralState);
            _coral = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
            _algae = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));
            _coral.gamePieceStates = new[] { coralState };
            _coral.intakes.Add(coralIntake);
            _algae.gamePieceStates = new[] { algaeState };
            _algae.intakes.Add(algaeIntake);

            _align = GetComponent<ReefscapeAutoAlign>();
            if (_align) _alignOffset = _align.offset;
            _drive = GetComponent<DriveController>();
            if (_drive) _driveTurn = _drive.rotationSpeedMultiplier;
            _robotBody = GetComponent<Rigidbody>();
            _reefs = new[] { "BlueReef", "RedReef" }.Select(GameObject.Find).Where(g => g).Select(g => g.transform).ToArray();
            _climbScorer = GetComponent<ClimbScorer>();
            _robotColliders = GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray();
            // the arm runs through the carriage on a long joint chain; a hit stops the robot within a step
            var stabilizer = GetComponent<JointStackStabilizer>();
            if (stabilizer)
            {
                stabilizer.solverIterations = 100;
                stabilizer.solverVelocityIterations = 40;
            }

            var group = Resources.Load<UnityEngine.Audio.AudioMixerGroup>("MainAudioMixer").audioMixer.FindMatchingGroups("RobotSounds")[0];
            AudioSource Source(AudioClip clip, bool loop, float volume)
            {
                var a = gameObject.AddComponent<AudioSource>();
                a.outputAudioMixerGroup = group;
                a.playOnAwake = false;
                a.loop = loop;
                a.clip = clip;
                a.volume = volume;
                return a;
            }
            _rollerAudio = Source(rollerClip, true, 0.35f);
            _stallAudio = Source(algaeStallClip, true, 0.3f);
            _servoAudio = Source(servoClip, true, 0.12f);
            _oneShot = Source(null, false, 0.6f);
        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPid);
            funnelLink.UpdatePid(funnelPid);
            climber.UpdatePid(climberPid);
            AimServos();
        }

        private void FixedUpdate()
        {
            var hasCoral = _coral.HasPiece();
            var hasAlgae = _algae.HasPiece();
            var intaking = IntakeAction.IsPressed();
            var climbing = CurrentSetpoint is ReefscapeSetpoints.Climb or ReefscapeSetpoints.Climbed;

            SelectTarget(hasCoral, hasAlgae);

            var armNow = ArmAngle();
            var elevatorNow = ElevatorCode();
            var atIntake = _target == intakeCoral && Mathf.Abs(armNow - intakeCoral.armAngle) < 25f && elevatorNow < ElevatorIntakeSafe + 2f;

            // Station coral: the wheels at the funnel's throat and the kicker run once the arm is down at them.
            var coralRun = CurrentSetpoint == ReefscapeSetpoints.Intake && CurrentRobotMode != ReefscapeRobotMode.Algae && !hasAlgae && !hasCoral;
            CurrentCoralStationMode.RequireIntaking = true;
            _coral.RequestIntake(coralIntake, coralRun && atIntake);
            LetFeedingCoralThrough();
            _coral.SetTargetState(coralState);

            var algaeRun = !hasAlgae && !hasCoral && intaking && IsAlgaeTarget();
            _algae.RequestIntake(algaeIntake, algaeRun);
            _algae.SetTargetState(algaeState);
            LetCaughtAlgaeIn();

            if (hasCoral && !_hadCoral && _coral.atTarget && coralSeatClip && Running()) _oneShot.PlayOneShot(coralSeatClip);
            _hadCoral = hasCoral && _coral.atTarget;

            UpdateAlignOffset();
            UpdateClimb(climbing);
            UpdateLift(hasCoral, hasAlgae, climbing, armNow, elevatorNow);
            UpdateRollers(coralRun && atIntake, algaeRun, hasAlgae);
            UpdateDrive(elevatorNow, climbing);
        }

        private bool Running() => BaseGameManager.Instance.RobotState != RobotState.Disabled;

        private bool IsAlgaeTarget() => _target == lowAlgae || _target == highAlgae || _target == floorAlgae || _target == stagedAlgae;

        private void SelectTarget(bool hasCoral, bool hasAlgae)
        {
            if (CurrentSetpoint != ReefscapeSetpoints.Place)
            {
                _released = false;
                _placeAskedAt = -1f;
            }
            switch (CurrentSetpoint)
            {
                // their INTAKING_CORAL state: down at the funnel when empty, carried at (10, 45) with a piece
                case ReefscapeSetpoints.Stow:
                    _target = hasCoral || hasAlgae ? transport : intakeCoral;
                    break;
                case ReefscapeSetpoints.Intake:
                    if (hasCoral || hasAlgae) _target = transport;
                    else _target = CurrentRobotMode == ReefscapeRobotMode.Algae ? floorAlgae : intakeCoral;
                    break;
                case ReefscapeSetpoints.L1: _target = l1; break;
                case ReefscapeSetpoints.L2: _target = l2; break;
                case ReefscapeSetpoints.L3: _target = l3; break;
                case ReefscapeSetpoints.L4: _target = l4; break;
                case ReefscapeSetpoints.LowAlgae: _target = lowAlgae; break;
                case ReefscapeSetpoints.HighAlgae: _target = highAlgae; break;
                case ReefscapeSetpoints.Stack: _target = stagedAlgae; break;
                case ReefscapeSetpoints.Processor: _target = processor; break;
                case ReefscapeSetpoints.Barge: _target = barge; break;
                case ReefscapeSetpoints.Climb:
                case ReefscapeSetpoints.Climbed:
                    _target = transport; // the hoop forward, clear of where the cage comes to lie (their code cuts the lift's power here)
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.Place:
                    PlacePiece();
                    break;
            }
        }

        // ---- the lift, with the interlocks from their LiftSubsystem

        // the joint reads 0 at the start pose and 181.5 at the funnel; it never goes round the back (-90.5)
        private float ArmAngle() => ArmStart - (Mathf.Repeat(arm.GetSingleAxisAngle(JointAxis.X) + 90.5f, 360f) - 90.5f);
        private float ElevatorCode() => elevator.GetElevatorHeight() / CodeInch;

        private bool NearReef()
        {
            foreach (var reef in _reefs)
            {
                if (!reef) continue;
                var d = reef.position - transform.position;
                d.y = 0f;
                if (d.magnitude < ReefClearance) return true;
            }
            return false;
        }

        private void UpdateLift(bool hasCoral, bool hasAlgae, bool climbing, float armNow, float elevatorNow)
        {
            var a = _target.armAngle;
            var e = _target.elevatorHeight;
            if (climbing) e = 0f;

            // Coming down from a level still against the reef would swing the hoop into its base and lift the wheels off
            // the floor (their traversal position): carry until clear.
            if (a < ArmReefClear && NearReef())
            {
                a = transport.armAngle;
                e = transport.elevatorHeight;
            }

            // Don't put the arm under until the elevator is down
            if (elevatorNow > ElevatorIntakeSafe + 4f && a < ArmSafeLowFront) a = ArmSafeLowFront;

            // Don't put the arm over until the elevator has moved up
            if (elevatorNow < ElevatorHighSafe)
            {
                if (hasAlgae && a > ArmSafeHighAlgae) a = ArmSafeHighAlgae;
                else if (elevatorNow > ElevatorLowSafe && e <= ElevatorLowSafe && a > ArmSafeMidFront) a = ArmSafeMidFront - 8f;
                else if (elevatorNow > ElevatorLowSafe && a > ArmSafeL3) a = ArmSafeL3;
            }

            // Don't put the elevator down while the arm is over
            if (elevatorNow > ElevatorHighSafe && e <= ElevatorHighSafe)
            {
                if (armNow > ArmSafeL3) e = ElevatorHighSafe + 1f;
            }
            else if (elevatorNow > ElevatorLowSafe && e <= ElevatorLowSafe)
            {
                if (hasAlgae && armNow > ArmSafeHighAlgae) e = ElevatorLowSafe + 1f;
                else if (armNow > ArmSafeMidFront) e = ElevatorLowSafe + 1f;
            }

            // Don't put the elevator up while the arm is under
            if (elevatorNow < ElevatorIntakeSafe && e >= ElevatorIntakeSafe && armNow < ArmSafeLowFront) e = 0f;

            elevator.SetTarget(e * CodeInch);
            // the arm never goes round the back: the elevator's cross members are there
            arm.SetTargetAngle(ArmStart - a).withAxis(JointAxis.X).noWrap(-90.5f).useCustomStartingOffset(0);
        }

        // ---- scoring

        private void UpdateAlignOffset()
        {
            if (!_align) return;
            var offset = _alignOffset;
            var from = CurrentSetpoint == ReefscapeSetpoints.Place ? _placeFrom : CurrentSetpoint;
            offset.z += from switch
            {
                ReefscapeSetpoints.L1 => l1Align,
                ReefscapeSetpoints.L2 => l2Align,
                ReefscapeSetpoints.L3 => l3Align,
                ReefscapeSetpoints.L4 => l4Align,
                ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.HighAlgae => algaeAlign,
                _ => 0f
            };
            // Going up, the arm's tip would sweep through the tail of a coral already on the level below: stand off (their
            // ApproachPosition) and close in once the lift is at height.
            if (CurrentSetpoint is ReefscapeSetpoints.L3 or ReefscapeSetpoints.L4 &&
                (Mathf.Abs(ElevatorCode() - _target.elevatorHeight) > 3f || Mathf.Abs(ArmAngle() - _target.armAngle) > 6f))
                offset.z -= approachStandOff;
            // reef algae sits in the middle of a face, between its two branch poles; L1 goes in the trough there too
            // (their Drive.java treats "L1 and Algae" together)
            if (from is ReefscapeSetpoints.L1 or ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.HighAlgae)
                offset.x += (AutoAlignLeftAction.IsPressed() ? 1f : AutoAlignRightAction.IsPressed() ? -1f : 0f) * 6.47f;
            _align.offset = offset;
        }

        private void PlacePiece()
        {
            if (!_released) _placeFrom = LastSetpoint;
            if (_released) return;

            if (_algae.HasPiece() && _algae.controller)
            {
                var t = _algae.controller.transform;
                var body = t.GetComponent<Rigidbody>();
                if (!_algae.ReleaseGamePieceWithForce(Vector3.zero)) return;
                StartCoroutine(PassRobotBriefly(t.gameObject, 0.4f));
                var speed = _placeFrom == ReefscapeSetpoints.Barge ? bargeEjectSpeed
                    : _placeFrom == ReefscapeSetpoints.Processor ? processorEjectSpeed : algaeDropSpeed;
                // the hoop faces forward and low at the processor: their 0.3 output just rolls it in
                var dir = _placeFrom == ReefscapeSetpoints.Processor ? transform.forward : algaeEject.forward;
                if (body) body.velocity = _robotBody.GetPointVelocity(body.position) + dir * speed;
                _algaePassing = null; // PassRobotBriefly lets it clear the robot and meets it again
                _released = true;
                _releasedAt = Time.time;
                return;
            }

            if (!_coral.HasPiece() || !_coral.controller) return;
            // Place pressed while the auto align is still closing in from its stand-off: let it get there first
            if (_placeAskedAt < 0f) _placeAskedAt = Time.time;
            var aligning = _align && (AutoAlignLeftAction.IsPressed() || AutoAlignRightAction.IsPressed());
            if (aligning && _align.getDistance() > 0.015f && Time.time - _placeAskedAt < MaxPlaceWait) return;
            var coralBody = _coral.controller.GetComponent<Rigidbody>();
            if (!_coral.ReleaseGamePieceWithForce(Vector3.zero)) return;
            if (_feeding) SetPassing(_feeding, false, funnelColliders);
            _feeding = null; // PassBriefly lets it clear the arm and meets it again
            var eject = _placeFrom switch
            {
                ReefscapeSetpoints.L1 => l1EjectSpeed,
                ReefscapeSetpoints.L2 => l2EjectSpeed,
                ReefscapeSetpoints.L3 => l3EjectSpeed,
                _ => l4EjectSpeed
            };
            if (_placeFrom == ReefscapeSetpoints.L1)
            {
                _armColliders ??= arm.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray();
                StartCoroutine(PassBriefly(coralBody.gameObject, 1f, _armColliders));
                coralBody.velocity = _robotBody.GetPointVelocity(coralBody.position) + coralEject.forward * eject + transform.forward * l1ForwardKick;
                coralBody.angularVelocity = transform.right * l1Tumble; // top over toward the reef, so it lies down in the trough
            }
            else StartCoroutine(PushCoralOut(coralBody, eject));
            _released = true;
            _releasedAt = Time.time;
        }

        // The tongue's wheels carry the coral off along its axis, on top of the arm's own motion. It meets the branch or
        // the trough as physics; the tongue stops touching it once it is off (it would bat it away on the swing back).
        private System.Collections.IEnumerator PushCoralOut(Rigidbody body, float speed)
        {
            if (!body) yield break;
            var armBody = arm.GetComponent<Rigidbody>();
            // it leaves the tongue's arm; the rest of the robot stays solid under it
            _armColliders ??= arm.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray();
            StartCoroutine(PassBriefly(body.gameObject, 1f, _armColliders));
            var start = Vector3.Dot(body.position - coralEject.position, coralEject.forward);
            var past = start;
            var relRot = Quaternion.Inverse(arm.transform.rotation) * body.rotation;
            bool Touches(Vector3 dir)
            {
                foreach (var hit in body.SweepTestAll(dir, speed * Time.fixedDeltaTime + 0.005f, QueryTriggerInteraction.Ignore))
                    if (!hit.collider.transform.IsChildOf(transform)) return true;
                return false;
            }
            body.useGravity = false;
            while (body && past < start + TongueCarry && !Touches(coralEject.forward))
            {
                past += speed * Time.fixedDeltaTime;
                var at = coralEject.position + coralEject.forward * past;
                body.position = at;
                body.rotation = arm.transform.rotation * relRot;
                body.velocity = armBody.GetPointVelocity(at) + coralEject.forward * speed;
                body.angularVelocity = armBody.angularVelocity;
                yield return new WaitForFixedUpdate();
            }
            if (body) body.useGravity = true;
            if (body) body.velocity = armBody.GetPointVelocity(body.position) + coralEject.forward * speed;
        }

        private System.Collections.IEnumerator PassRobotBriefly(GameObject piece, float seconds) => PassBriefly(piece, seconds, _robotColliders);

        private System.Collections.IEnumerator PassBriefly(GameObject piece, float seconds, Collider[] colliders)
        {
            // the release turns the piece's colliders back on a step or two later, which clears an ignore set before it
            for (var i = 0; i < 6 && piece; i++)
            {
                SetPassing(piece, true, colliders);
                yield return new WaitForFixedUpdate();
            }
            yield return new WaitForSeconds(seconds);
            if (piece && !IsHeld(piece)) SetPassing(piece, false, colliders);
        }

        private bool IsHeld(GameObject piece) =>
            (_coral.HasPiece() && _coral.controller && _coral.controller.gameObject == piece) ||
            (_algae.HasPiece() && _algae.controller && _algae.controller.gameObject == piece);

        private static void SetPassing(GameObject piece, bool pass, Collider[] others)
        {
            if (others == null) return;
            foreach (var c in piece.GetComponentsInChildren<Collider>(true))
            foreach (var r in others)
                if (r) Physics.IgnoreCollision(c, r, pass);
        }

        // the wheels the funnel's coral rests on are solid, but the one they have caught goes on between them
        private void LetFeedingCoralThrough()
        {
            _armColliders ??= arm.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray();
            var caught = coralIntake.hasGamePiece ? coralIntake.GamePiece : null;
            if (_feeding && _feeding != caught && !IsHeld(_feeding))
            {
                SetPassing(_feeding, false, funnelColliders);
                SetPassing(_feeding, false, _armColliders);
                _feeding = null;
            }
            if (caught && caught != _feeding)
            {
                // onto the tongue, inside the coral: it slides past the arm's plates and the funnel's wheels
                SetPassing(caught, true, funnelColliders);
                SetPassing(caught, true, _armColliders);
                _feeding = caught;
            }
        }

        // a ball the hoop's wheels have caught sinks into them and the rollers: it passes the arm's colliders until held
        private void LetCaughtAlgaeIn()
        {
            _armColliders ??= arm.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToArray();
            var caught = algaeIntake.hasGamePiece ? algaeIntake.GamePiece : null;
            if (_algaePassing && _algaePassing != caught && !IsHeld(_algaePassing))
            {
                SetPassing(_algaePassing, false, _armColliders);
                _algaePassing = null;
            }
            if (caught && caught != _algaePassing)
            {
                SetPassing(caught, true, _armColliders);
                _algaePassing = caught;
            }
        }

        // ---- the climb

        // A press folds the funnel down (their two servos, about 3 s) and then the head drops out on its elastics; driving a
        // cage upright into the head's fork seats it past the latches; the next press winds the head back up, which turns
        // the cage in over the robot and lifts it. Nothing holds but the fork's and the latches' colliders.
        private void UpdateClimb(bool climbing)
        {
            var dt = Time.fixedDeltaTime;
            var running = Running();
            if (running)
                _servo = Mathf.MoveTowards(_servo, climbing ? servoStroke : 0f, servoSpeed * dt);
            funnelLink.SetTargetAngle(-CrankAngle(_servo)).withAxis(JointAxis.X).useCustomStartingOffset(0);
            if (_servoAudio && _servoAudio.clip)
            {
                var moving = running && climbing && _servo < servoStroke;
                if (moving && !_servoAudio.isPlaying) _servoAudio.Play();
                else if (!moving && _servoAudio.isPlaying) _servoAudio.Stop();
            }

            if (running)
            {
                if (CurrentSetpoint == ReefscapeSetpoints.Climbed) _head = Mathf.MoveTowards(_head, 0f, winchRate * dt);
                else if (CurrentSetpoint == ReefscapeSetpoints.Climb) _head = _servo >= servoStroke ? headOutAngle : 0f;
                else _head = 0f;
            }
            climber.SetTargetAngle(-_head).withAxis(JointAxis.Z).noWrap(90f).useCustomStartingOffset(0);

            if (!climbing) _latched = false;
            else if (!_latched && CurrentSetpoint == ReefscapeSetpoints.Climb && cageSeat)
            {
                var c = cageSeat.transform.TransformPoint(cageSeat.center);
                var half = Vector3.Scale(cageSeat.size, cageSeat.transform.lossyScale) / 2f;
                foreach (var hit in Physics.OverlapBox(c, half, cageSeat.transform.rotation))
                    if (hit.CompareTag("Cage")) { _latched = true; break; }
            }
            foreach (var l in cageLatches) if (l) l.enabled = _latched;
            foreach (var v in latchVisuals) if (v) v.localRotation = Quaternion.Euler(_latched ? 0f : 35f, 0f, 0f);
            if (_latched && !_wasLatched && cageLatchClip) _oneShot.PlayOneShot(cageLatchClip);
            _wasLatched = _latched;

            var wheels = running && CurrentSetpoint == ReefscapeSetpoints.Climb && !_latched ? rollerVisualSpeed : 0f;
            foreach (var w in cageWheels) if (w) w.VelocityRoller(wheels);
        }

        // The lower link's angle from the servo's length: the triangle of its pivot, the fixed pin and the gusset pin.
        private float CrankAngle(float stroke)
        {
            float Gusset(float len)
            {
                var cos = (gussetRadius * gussetRadius + fixedPinDistance * fixedPinDistance - len * len) / (2f * gussetRadius * fixedPinDistance);
                return Mathf.Acos(Mathf.Clamp(cos, -1f, 1f)) * Mathf.Rad2Deg;
            }
            return Gusset(servoLengthAtZero + stroke) - Gusset(servoLengthAtZero);
        }

        // the servo's body turns on its fixed pin and its rod on the gusset's, each lined up on the other pin
        private void AimServos()
        {
            void Aim(Transform body, Transform rod, Transform fixedPin, Transform gussetPin)
            {
                if (!body || !rod || !fixedPin || !gussetPin) return;
                var along = gussetPin.position - fixedPin.position;
                if (along.sqrMagnitude < 1e-6f) return;
                var look = Quaternion.LookRotation(along, transform.up);
                body.SetPositionAndRotation(fixedPin.position, look);
                rod.SetPositionAndRotation(gussetPin.position, look);
            }
            Aim(servoBody, servoRod, servoFixedPin, servoGussetPin);
            Aim(servoBody2, servoRod2, servoFixedPin2, servoGussetPin2);
        }

        private void UpdateRollers(bool coralRun, bool algaeRun, bool hasAlgae)
        {
            var running = Running();
            var placing = CurrentSetpoint == ReefscapeSetpoints.Place && _released && Time.time - _releasedAt < 0.3f;
            var tongue = !running ? 0f : coralRun ? -0.9f : placing ? 0.6f : algaeRun ? -1f : hasAlgae ? -0.09f : 0f;
            foreach (var w in tongueWheels) if (w) w.VelocityRoller(tongue * rollerVisualSpeed);
            foreach (var w in algaeWheels) if (w) w.VelocityRoller(tongue * rollerVisualSpeed);
            var funnel = running && coralRun ? 0.5f : running && CurrentSetpoint != ReefscapeSetpoints.Climb && CurrentSetpoint != ReefscapeSetpoints.Climbed ? -0.15f : 0f;
            foreach (var w in funnelWheels) if (w) w.VelocityRoller(funnel * rollerVisualSpeed);

            Loop(_rollerAudio, running && (coralRun || algaeRun || placing));
            Loop(_stallAudio, running && hasAlgae);
        }

        private static void Loop(AudioSource a, bool on)
        {
            if (!a || !a.clip) return;
            if (on && !a.isPlaying) a.Play();
            else if (!on && a.isPlaying) a.Stop();
        }

        // Their teleop: 25% drive and 50% turn above 25 code inches. While winding on the cage, no driving at all.
        private void UpdateDrive(float elevatorNow, bool climbing)
        {
            // MoSim's body drag of 3 tops every robot out near 3.2 m/s. Their code runs 4.68 m/s (6.82:1, Kraken X60): less
            // drag at speed reaches it, and low-speed handling and the auto align stay as they are.
            _robotBody.drag = Mathf.Lerp(3f, topSpeedDrag, Mathf.InverseLerp(2.4f, 3f, _robotBody.velocity.magnitude));
            // their slowdown is on the driver's sticks: the auto align isn't slowed by it
            var slow = elevatorNow > DriveSlowHeight && !AutoAlignLeftAction.IsPressed() && !AutoAlignRightAction.IsPressed();
            var scale = CurrentSetpoint == ReefscapeSetpoints.Climbed ? 0f : climbing ? 0.5f : slow ? 0.25f : 1f;
            var turn = CurrentSetpoint == ReefscapeSetpoints.Climbed ? 0f : climbing ? 0.5f : slow ? 0.5f : 1f;
            if (_drive && !Mathf.Approximately(scale, _driveScale))
            {
                _driveScale = scale;
                _drive.SetDriveMp(scale);
                _drive.rotationSpeedMultiplier = _driveTurn * turn;
            }
        }
    }
}
