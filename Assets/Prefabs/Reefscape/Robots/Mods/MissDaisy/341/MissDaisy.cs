using System.Linq;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.Enums;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.MissDaisy._341
{
    /// <summary>
    /// 341 Miss Daisy XXIII: continuous elevator, shoulder arm with a roller-claw wrist, over-the-bumper coral floor
    /// intake at the back that hands coral up to the claw, deep climber with sprung harpoon hooks.
    /// The front (+Z) is the scoring side, as on the real robot. Setpoint shoulder angles read "+ = toward the front";
    /// reef setpoints mirror to the intake side when that side faces the reef (never for L1).
    /// Setpoint changes are sequenced like 341's Superstructure.poseAdjust: the arm stands up before the elevator
    /// travels low down, the wrist only turns once the arm is clear, the intake deploys before the arm swings over it,
    /// and scoring "dunks" (shoulder first, then the elevator) before the claw lets go.
    /// </summary>
    public class MissDaisy : ReefscapeRobotBase
    {
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint shoulder, wrist, intakePivot, climber;
        [Tooltip("Intake roller physics (collide with game pieces only).")]
        [SerializeField] private GenericRoller[] intakeRollers;
        [Tooltip("Intake wheel and belt meshes, spun for show.")]
        [SerializeField] private GenericAnimationJoint[] intakeRollerVisuals;
        [Tooltip("Claw compliant wheels, spun for show; a positive speed pulls a piece in.")]
        [SerializeField] private GenericAnimationJoint[] clawWheels;
        [Tooltip("Floor coral's way in after the pickup: under the front roller, under the top roller, out of the top.")]
        [SerializeField] private Transform[] coralPath;
        [Tooltip("The floor intake's target: runs just ahead of the coral along the path, then sits on the claw state.")]
        [SerializeField] private Transform coralCarrot;
        [Tooltip("The coral prefab's long axis (its own frame) that the path's rotations were built for.")]
        [SerializeField] private Vector3 coralPathAxis = Vector3.forward;
        [Tooltip("How far ahead of the coral the floor intake's target runs (m).")]
        [SerializeField] private float coralPathLead = 0.11f;
        [Tooltip("The floor coral's last stretch: straight up into the claw's mouth from this far out (m).")]
        [SerializeField] private float clawEntry = 0.08f;
        [Tooltip("The floor intake's pull (its intake force, in/s^2 scale) when the coral lines up with the intake.")]
        [SerializeField] private float coralPullForce = 38f; // x 0.0254 must stay under 1 (stable pull); faster comes from the intake speed
        [Tooltip("The sprung roller arms and top-roller flap (their tubing): spring N m/rad and damping N m s/rad.")]
        [SerializeField] private Rigidbody rollerArmsBody;
        [SerializeField] private Rigidbody intakeFlapBody;
        [SerializeField] private float armsSpring = 6f, armsDamping = 1.2f, flapSpring = 0.6f, flapDamping = 0.15f;
        [Tooltip("Sprung harpoon hooks on the climber: the cage post pushes them in and they snap back behind it.")]
        [SerializeField] private GenericAnimationJoint[] climberHooks;

        // Motion profiles matched to match footage (Behind the Bumpers, 2025): intake pose to L4 prescore - ~168 deg of
        // shoulder and ~1.2 m of elevator - in ~0.6 s; the dunk ~25 deg in ~0.45 s. 341's code allows 2 rot/s, 9 rot/s^2
        // on the shoulder; the arm never gets near that.
        [Tooltip("Shoulder motion profile: cruise (rad/s) and acceleration (rad/s^2); the dunk (L2-L4 score) cruises slower.")]
        [SerializeField] private float shoulderCruise = 9f;
        [SerializeField] private float shoulderAccel = 85f;
        [SerializeField] private float dunkCruise = 3.5f;
        [Tooltip("Elevator motion profile (its target is moved along this, like MotionMagic): cruise m/s, acceleration m/s^2.")]
        [SerializeField] private float elevatorCruise = 4.5f;
        [SerializeField] private float elevatorAccel = 25f;
        [SerializeField] private float dunkElevatorCruise = 0.5f; // following the arm down onto the branch, not dropping

        [Header("PIDs")]
        [SerializeField] private PidConstants shoulderPid;
        [SerializeField] private PidConstants wristPid, intakePid, climberPid, climberDeployPid;

        [Header("Setpoints")]
        [SerializeField] private MissDaisySetpoint stow;
        [SerializeField] private MissDaisySetpoint groundIntake, hpIntake;
        [SerializeField] private MissDaisySetpoint l1, l2, l3, l4;
        [Tooltip("Where the claw dunks to from the matching prescore before it lets go.")]
        [SerializeField] private MissDaisySetpoint l2Score, l3Score, l4Score;
        [SerializeField] private MissDaisySetpoint groundAlgae, lollipop, lowAlgae, highAlgae, processor;
        [Tooltip("Barge: arm down low out the front, then swung up and over while the elevator shoots up.")]
        [SerializeField] private MissDaisySetpoint flingStart, flingEnd;
        [SerializeField] private MissDaisySetpoint algaeHolding;
        [SerializeField] private MissDaisySetpoint climbPrep, climbed;

        [Header("Intakes")]
        [SerializeField] private ReefscapeGamePieceIntake groundCoralIntake;
        [SerializeField] private ReefscapeGamePieceIntake hpCoralIntake, algaeIntake;

        [Header("Game Piece States")]
        [SerializeField] private GamePieceState clawCoralState;
        [SerializeField] private GamePieceState algaeState;

        [Header("Tuning")]
        [Tooltip("Forward points out of the claw tip: placed pieces are pushed this way.")]
        [SerializeField] private Transform clawEject;
        [SerializeField] private float rollerSpeed = 3000f;
        [SerializeField] private float rollerVisualSpeed = 1200f;
        [SerializeField] private float clawWheelSpeed = 900f;
        [SerializeField] private float coralCatchClear = 0.1f; // m: over a step's travel at the rollers' speed
        [SerializeField] private float coralCatchSquare = 5f; // deg
        private bool _coralTurning;
        private readonly System.Collections.Generic.Dictionary<Rigidbody, (Collider[] cols, LayerMask[] masks)> _centringCoral = new();
        [SerializeField] private float coralStallTime = 1f; // s: longer than any normal pull (0.75), which has to lift the flap on its own
        [SerializeField] private float coralCentreSpeed = 0.6f; // m/s
        private GameObject _stallPiece;
        private float _stallBestS, _stallAt;
        private Transform _clawLetsIn;
        private float _clawClearUntil;
        private readonly System.Collections.Generic.List<(Collider, Collider)> _clawLetsInPairs = new();
        [SerializeField] private float coralTurnSpeed = 3f; // rad/s: an end-on coral squaring up at the mouth (at 6 it batted a coral beside it away)
        [SerializeField] private float coralEjectForce = 0.25f;
        [SerializeField] private float coralEjectTime = 0.6f; // L1: a short level push out of the claw
        [SerializeField] private float coralEjectSpeed = 0.4f; // L1: a gentle level push (0.9 bounced it off the back of the trough; at 0.3 a second coral stayed on the first under a scored L2)
        [Tooltip("L2-L4: the claw's gentle let-go (341's 4 V of 12) straight out of its mouth, m/s and s; the branch " +
                 "already inside the coral holds it as the claw backs off.")]
        [SerializeField] private float scoreEjectSpeed = 0.25f;
        [SerializeField] private float scoreEjectTime = 0.5f;
        [Tooltip("A placed coral's push-out speed (m/s) from anything it overlapped while gripped.")]
        [SerializeField] private float releasedDepenetration = 0.3f;
        [Tooltip("341's ScoreBack: the claw lets go within this many degrees of the score angle (L2/L3, L4). L2/L3 is " +
                 "9 here, not 7: a coral pressed on the branch can stop the arm just over 7 short, and it never let go.")]
        [SerializeField] private float scoreReleaseTolerance = 9f;
        [SerializeField] private float l4ReleaseTolerance = 10f;
        [SerializeField] private float algaeEjectForce = 10f;
        [SerializeField] private float processorEjectForce = 3f;
        [Tooltip("Algae launch speed (m/s, relative to the robot) out of the fling.")]
        [SerializeField] private float flingSpeed = 4.2f;
        [Tooltip("Degrees above horizontal the algae leaves the fling at, toward the intake side.")]
        [SerializeField] private float flingPitch = 68f;
        [Tooltip("The fling lets go once the shoulder has swung up past this angle (deg toward the front).")]
        [SerializeField] private float flingReleaseAngle = 15f;
        [Tooltip("The fling lets go this far (m) below the top of its elevator travel.")]
        [SerializeField] private float flingReleaseBelowTop = 0.4f;
        [Tooltip("Inches from a reef branch pole to the face centre, where the algae sits.")]
        [SerializeField] private float reefFaceHalfWidth = 6.47f;
        [Tooltip("Intake angle while scoring L2 over the intake side (341 swings it out to clear the coral).")]
        [SerializeField] private float mirroredL2IntakeAngle = -18f; // 341: coralIntakeAngle 72 deg with 90 = stowed
        [Tooltip("Degrees a harpoon hook can fold in.")]
        [SerializeField] private float climbSeatAngle = 150f;
        [SerializeField] private float climbSeatTime = 1.5f;
        private float _climbedAt;
        [SerializeField] private float maxHookTravel = 45f;
        [Tooltip("How fast a hook folds in under a bar and springs back out (deg/s).")]
        [SerializeField] private float hookSpeed = 100f; // deg/s (Seb's playtest setting)
        [Tooltip("Climbed: the body's centre of mass goes here (robot frame, m), on the intake and arm side under the top of " +
                 "the cage (only while climbed, as 694 v3 does; driving it stays automatic).")]
        [SerializeField] private Vector3 climbedCentreOfMass = new Vector3(0f, 0.098f, -0.32f);
        [Tooltip("Other coral the running rollers touch are drawn in along the way up at this speed (m/s).")]
        [SerializeField] private float otherCoralSpeed = 1f;

        [Header("Robot Audio")]
        [Tooltip("Loops while the floor intake's rollers or the claw's wheels run.")]
        [SerializeField] private AudioClip intakeRollerClip;
        [Tooltip("A climb hook snapping shut behind a cage post.")]
        [SerializeField] private AudioClip hookLatchClip;

        private const float InchesPerMeter = 39.3700787f;

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode
            _coralController, _algaeController;

        private MissDaisySetpoint _target;
        private bool _mirror;
        private bool _floorCoral;
        private bool _released;
        private float[] _hookAngles = new float[0];
        private readonly Collider[] _hookHits = new Collider[16];
        private Vector3 _pathStart;
        private Vector3 _coralLocalAxis;
        private GameObject _axisPiece;
        private GameObject _pathPiece;
        private readonly Quaternion[] _hingeRest = new Quaternion[2];
        private readonly bool[] _hingeRestSet = new bool[2];
        private (Collider c, LayerMask ex)[] _clawColliders;
        private bool _clawPassing;
        private float _releasedAt = -10f;
        private readonly System.Collections.Generic.Dictionary<Collider, LayerMask> _passingReef = new();
        private float _pathLost = float.NegativeInfinity;
        private float _pathS = -1f;
        private float _intakeTouchAt = float.NegativeInfinity, _rollerTouchAt = float.NegativeInfinity;
        private float _notHeldSince = float.NaN;
        private float _lastS = float.NaN, _lastW = float.NaN, _lastI = float.NaN;
        private CapsuleCollider _heldCoralBody;
        private bool _comShifted;
        private readonly Collider[] _otherHits = new Collider[32];
        private readonly System.Collections.Generic.HashSet<Rigidbody> _otherCoral = new();
        private AudioSource _intakeRollerAudio, _oneShotAudio;
        private readonly System.Collections.Generic.Dictionary<Collider, LayerMask> _turningCoral = new();
        [SerializeField] private float l1LiftSpeed = 0.25f, l1MaxLift = 0.09f, l1LiftMargin = 0.03f;
        private float _l1Lift, _l1LiftTo;
        [SerializeField] private float hpStowDelay = 0.5f; // s: the coral settles in the claw before the arm swings back
        private float _hpHeldAt = float.NaN;
        [SerializeField] private float climbedLockHeight = 0.05f; // m off the carpet: hanging, nothing on the robot moves any more
        private bool _climbLocked;
        private float _carpetY;
        private Collider[] _intakeContacts;
        private readonly System.Collections.Generic.List<Vector3> _pathPts = new();
        private readonly System.Collections.Generic.List<Quaternion> _pathRots = new();
        private bool _prescored;
        private float _lastShoulder, _shoulderStill, _shoulderSlowed;
        private ConfigurableJoint _shoulderJoint;
        private float _shoulderVel;
        private float _shoulderTarget;
        private float _elevCmd = float.NaN, _elevVel;
        private float _flingHold = float.NaN; // shoulder angle the arm stops at once the algae is away
        private ReefscapeSetpoints _placeFrom;
        private Vector3 _lastClawPos, _clawVelocity;

        private Transform _blueReef, _redReef;
        private ReefscapeAutoAlign _align;
        private Rigidbody _robotBody;
        private Vector3 _alignOffset;

        protected override void Start()
        {
            base.Start();

            CurrentCoralStationMode.RotationVariance = 0f; // station coral comes out flat, no spin on it
            shoulder.SetPid(shoulderPid);
            wrist.SetPid(wristPid);
            intakePivot.SetPid(intakePid);
            climber.SetPid(climberPid);

            _target = stow;

            RobotGamePieceController.SetPreload(clawCoralState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
            _algaeController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));

            _coralController.gamePieceStates = new[] { clawCoralState };
            _coralController.intakes.Add(groundCoralIntake);
            _coralController.intakes.Add(hpCoralIntake);

            _algaeController.gamePieceStates = new[] { algaeState };
            _algaeController.intakes.Add(algaeIntake);

            var robotSounds = Resources.Load<UnityEngine.Audio.AudioMixerGroup>("MainAudioMixer").audioMixer.FindMatchingGroups("RobotSounds")[0];
            AudioSource Source(Component host, AudioClip clip, bool loop)
            {
                var source = host.gameObject.AddComponent<AudioSource>();
                source.outputAudioMixerGroup = robotSounds;
                source.playOnAwake = false;
                source.loop = loop;
                source.clip = clip;
                source.spatialBlend = 1f; // from where the mechanism is
                return source;
            }
            _intakeRollerAudio = Source(groundCoralIntake, intakeRollerClip, true);
            _intakeRollerAudio.volume = 0.5f;
            _oneShotAudio = Source(climber, null, false);

            _align = GetComponent<ReefscapeAutoAlign>();
            _robotBody = GetComponent<Rigidbody>();
            _carpetY = _robotBody.position.y;
            if (_align) _alignOffset = _align.offset;

            var blue = GameObject.Find("BlueReef");
            var red = GameObject.Find("RedReef");
            _blueReef = blue ? blue.transform : null;
            _redReef = red ? red.transform : null;
            _lastClawPos = clawEject.position;
        }

        protected override void Update()
        {
            base.Update();
            UpdateHooks();
        }

        // The harpoon hooks (sprung, constantly pushed out): a cage bar on a hook's sloped nose folds it in about its
        // pivot (local +Y); with nothing on the nose the spring swings it back out to its stop, behind the bar. Only
        // solid cage parts push - the framework's SpringLoaded also counts field trigger zones, which held the hooks
        // folded inside the barge zone.
        private void UpdateHooks()
        {
            if (_hookAngles.Length != climberHooks.Length) _hookAngles = new float[climberHooks.Length];
            for (var i = 0; i < climberHooks.Length; i++)
            {
                var hook = climberHooks[i];
                if (!hook) continue;
                var nose = hook.transform.Find("Move Away");
                var box = nose ? nose.GetComponent<BoxCollider>() : null;
                if (!box) continue;
                var n = Physics.OverlapBoxNonAlloc(nose.TransformPoint(box.center), Vector3.Scale(box.size, nose.lossyScale) / 2,
                    _hookHits, nose.rotation, ~0, QueryTriggerInteraction.Ignore);
                // A post folds the hook only by pushing on its nose, from the tip side of the barb. Pulled back against
                // the barb (climbing) it drives the hook onto its stop, and the hook holds it - it does not fold open.
                var pushed = false;
                var holder = hook.transform.parent;
                for (var k = 0; k < n && !pushed; k++)
                {
                    if (!_hookHits[k].CompareTag("Cage")) continue;
                    var onPost = _hookHits[k].ClosestPoint(holder.position);
                    pushed = holder.InverseTransformPoint(onPost).z > -0.032f; // the barb is 3.2 cm back from the pivot
                }
                // Past square on the pull a post can only press on the barb from inside: the hook stays shut.
                if (_target == climbed && Mathf.DeltaAngle(0f, climber.GetSingleAxisAngle(JointAxis.X)) < climbSeatAngle - 5f && Mathf.DeltaAngle(0f, climber.GetSingleAxisAngle(JointAxis.X)) > 0f) pushed = false;
                var was = _hookAngles[i];
                _hookAngles[i] = Mathf.MoveTowards(_hookAngles[i], pushed ? maxHookTravel : 0f, hookSpeed * Time.deltaTime);
                if (was > 0f && _hookAngles[i] == 0f && !pushed) PlayOnce(hookLatchClip); // it springs back behind the post
                hook.transform.localRotation = Quaternion.AngleAxis(_hookAngles[i], Vector3.up);
            }
        }

        private void LateUpdate()
        {
            shoulder.UpdatePid(shoulderPid);
            wrist.UpdatePid(wristPid);
            intakePivot.UpdatePid(intakePid);
            climber.UpdatePid(_target == climbed ? climberPid : climberDeployPid); // a slow loaded pull, a quick deploy
        }

        private void FixedUpdate()
        {
            _clawVelocity = (clawEject.position - _lastClawPos) / Time.fixedDeltaTime;
            _lastClawPos = clawEject.position;

            // Hanging from the cage nothing moves any more, whatever is pressed. (Put back on the carpet, it is free again.)
            _climbLocked = (_climbLocked || CurrentSetpoint == ReefscapeSetpoints.Climbed) && _robotBody.position.y - _carpetY > climbedLockHeight;
            if (_climbLocked)
            {
                if (CurrentSetpoint != ReefscapeSetpoints.Climbed) SetState(ReefscapeSetpoints.Climbed);
                _coralController.RequestIntake(groundCoralIntake, false);
                _coralController.RequestIntake(hpCoralIntake, false);
                _algaeController.RequestIntake(algaeIntake, false);
                SetSetpoint(climbed, false);
                foreach (var roller in intakeRollers)
                    if (roller) roller.SetAngularVelocity(0f);
                foreach (var roller in intakeRollerVisuals)
                    if (roller) roller.VelocityRoller(0f);
                foreach (var w in clawWheels)
                    if (w) w.VelocityRoller(0f);
                Loop(_intakeRollerAudio, false, 1f);
                SpringHinges();
                ShiftCentreOfMassForClimb();
                UpdateSetpoints();
                return;
            }

            var hasCoral = _coralController.HasPiece();
            var hasAlgae = _algaeController.HasPiece();
            var intaking = IntakeAction.IsPressed();
            var groundMode = CurrentCoralStationMode.DropType == DropType.Ground;
            var coralIntaking = intaking && CurrentSetpoint == ReefscapeSetpoints.Intake &&
                                CurrentRobotMode != ReefscapeRobotMode.Algae && !hasAlgae;
            var groundIntaking = coralIntaking && groundMode && !hasCoral;

            // Floor coral: past the front roller and up the belts (the claw already waiting over the top of the intake),
            // then up into the claw. Station coral drops straight into the sideways claw.
            UpdateCoralCarrot(hasCoral);
            // once the intake has a coral (it is pushing the flap up) it carries on to the claw whatever the driver does
            // Only a coral the rollers have brought into the channel is held on to after intake is let go
            var floorTransit = groundCoralIntake && groundCoralIntake.hasGamePiece && !hasCoral;
            _coralController.RequestIntake(groundCoralIntake, (groundIntaking && AtPose(groundIntake, 8f)) || floorTransit);
            _coralController.RequestIntake(hpCoralIntake, coralIntaking && !groundMode && !hasCoral && AtPose(hpIntake, 8f));
            _algaeController.RequestIntake(algaeIntake, intaking && IsAlgaeTarget() && !hasCoral && !hasAlgae);
            if (!hasCoral) _floorCoral = groundIntaking;
            var coming = groundCoralIntake && groundCoralIntake.hasGamePiece ? groundCoralIntake.GamePiece
                : hpCoralIntake && hpCoralIntake.hasGamePiece ? hpCoralIntake.GamePiece : null;
            if (coming) ClawLetsCoralIn(coming.transform);
            else if (!hasCoral && Time.time > _clawClearUntil) ClawLetsCoralIn(null);
            var coralState = _coralController.GetCurrentState();
            var inClaw = hasCoral && _coralController.atTarget && coralState != null && coralState.name == clawCoralState.name;
            if (inClaw) _floorCoral = false;
            // Held, it goes with the claw every physics step (the framework re-attaches it per rendered frame: through a
            // long frame it was left hanging, then jumped back, or was let go from where it had been left)
            if (inClaw && _coralController.controller)
            {
                var held = _coralController.controller.transform;
                var heldBody = held.GetComponent<Rigidbody>();
                if (heldBody && held.parent)
                {
                    heldBody.position = held.parent.position;
                    heldBody.rotation = held.parent.rotation;
                }
            }
            _coralController.SetTargetState(clawCoralState);
            _algaeController.SetTargetState(algaeState);

            // A coral on its way to the claw holds the robot where it is: nothing moves until it is secured.
            var coralInTransit = hasCoral && !inClaw;
            HeldCoralPassesReef(hasCoral && !_released);
            HeldCoralHitsCoral(hasCoral && !_released); // a held coral never passes through other coral
            if (!coralInTransit) SelectSetpoint(hasCoral, groundMode);
            if (floorTransit) SetSetpoint(groundIntake, false); // stay put until the coral is in the claw

            var floorIntakeRunning = (groundIntaking && AtPose(groundIntake, 8f)) || floorTransit;
            SpringHinges();
            ClawPassesReef(CurrentSetpoint == ReefscapeSetpoints.Place && !IsL1Place() && (hasCoral || Time.time - _releasedAt < 0.5f));
            var rollers = BaseGameManagerRunning() && (floorIntakeRunning || (_floorCoral && coralInTransit)) ? 1f : 0f;
            foreach (var roller in intakeRollers)
                if (roller) roller.SetAngularVelocity(-rollers * rollerSpeed); // the coral side of each roller runs inward and up
            foreach (var roller in intakeRollerVisuals)
                if (roller) roller.VelocityRoller(rollers * rollerVisualSpeed);
            var clawIntaking = floorIntakeRunning || coralInTransit ||
                               (coralIntaking && !groundMode && !hasCoral && AtPose(hpIntake, 8f)) ||
                               (intaking && IsAlgaeTarget() && !hasAlgae);
            var claw = clawIntaking ? 1f : CurrentSetpoint == ReefscapeSetpoints.Place && _released ? -1f : 0f;
            foreach (var w in clawWheels)
                if (w) w.VelocityRoller(claw * clawWheelSpeed);
            Loop(_intakeRollerAudio, rollers != 0f || claw != 0f, 1f);
            if (rollers != 0f) PullOtherCoralOnRollers();
            else if (_centringCoral.Count > 0)
                foreach (var rb in _centringCoral.Keys.ToList())
                    RestoreCentring(rb);

            ShiftCentreOfMassForClimb();
            UpdateAlignOffset();
            UpdateSetpoints();
        }

        // The floor intake pulls its coral toward the carrot, kept a short lead ahead of the coral along the path (the
        // coral's own progress, never backwards), so it is drawn round the rollers and turns gradually to the claw's
        // grip. At the end the carrot sits on the claw state, where the coral is secured.
        private void UpdateCoralCarrot(bool hasCoral)
        {
            if (!coralCarrot || coralPath == null || coralPath.Length == 0) return;
            var piece = groundCoralIntake && groundCoralIntake.hasGamePiece ? groundCoralIntake.GamePiece : null;
            if (!piece)
            {
                _coralTurning = false;
                _stallPiece = null; // (the same coral comes round again, its old progress would read as a stall)
                CoralTurnsClearOfRobot(null, false);
                // a coral that slipped out of range keeps its place on the path for a moment, in case it is re-grabbed
                if (_pathPiece && float.IsNegativeInfinity(_pathLost)) _pathLost = Time.time;
                if (hasCoral || (_pathPiece && Time.time - _pathLost > 1f)) { _pathS = -1f; _pathPiece = null; }
                if (hasCoral) coralCarrot.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity); // on the claw state
                // idle: parked inside the intake, well clear of the pickup zone - a coral detected within the catch
                // radius of the target would count as caught on its first frame and jump straight to the claw
                else if (!_pathPiece)
                {
                    var park = coralPath[Mathf.Min(1, coralPath.Length - 1)];
                    coralCarrot.SetPositionAndRotation(park.position, park.rotation);
                }
                return;
            }

            var end = coralCarrot.parent;
            var flip = Quaternion.AngleAxis(180f, Vector3.up); // a coral held either way round looks the same
            Quaternion Facing(Quaternion r) => Quaternion.Angle(r * flip, end.rotation) < Quaternion.Angle(r, end.rotation) ? r * flip : r;
            // a coral re-grabbed straight after slipping out of range carries on from where it got to
            if (piece != _pathPiece) { _pathS = -1f; _pathPiece = piece; }
            // its own long axis: spawned and preplaced corals are not built the same way (a cached axis from another
            // coral steered the wrong one, and the coral came in end-on and jammed under the roller)
            if (piece != _axisPiece) { _coralLocalAxis = CoralLocalAxis(piece.transform); _axisPiece = piece; }
            // continuous collision while it is pulled: at ~4 cm a step it would tunnel through the flap's thin belts
            var pulled = piece.GetComponentInChildren<Rigidbody>();
            if (pulled && pulled.collisionDetectionMode == CollisionDetectionMode.Discrete)
                pulled.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _pathLost = float.NegativeInfinity;
            if (_pathS < 0) { _pathStart = piece.transform.position; _pathS = 0f; }
            _pathPts.Clear();
            _pathRots.Clear();
            _pathPts.Add(_pathStart);
            _pathRots.Add(Facing(coralPath[0].rotation));
            // (it is past the front roller by the time the intake has it: up from where it is)
            foreach (var p in coralPath.Skip(1)) { _pathPts.Add(p.position); _pathRots.Add(Facing(p.rotation)); }
            // into the claw's mouth along its axis (341: the intake tosses the coral up into the waiting claw)
            _pathPts.Add(end.position + clawEject.forward * clawEntry);
            _pathRots.Add(end.rotation);
            _pathPts.Add(end.position);
            _pathRots.Add(end.rotation);

            // the coral's progress: closest point on the path from where it got to (no jumping ahead or back)
            var pos = piece.transform.position;
            float run = 0f, best = float.MaxValue, bestS = _pathS;
            for (var i = 0; i < _pathPts.Count - 1; i++)
            {
                var a = _pathPts[i];
                var seg = _pathPts[i + 1] - a;
                var len = seg.magnitude;
                if (run + len >= _pathS - 0.05f && run <= _pathS + 0.25f && len > 1e-5f)
                {
                    var t = Mathf.Clamp01(Vector3.Dot(pos - a, seg) / (len * len));
                    var d = (a + seg * t - pos).sqrMagnitude;
                    if (d < best) { best = d; bestS = run + t * len; }
                }
                run += len;
            }
            _pathS = Mathf.Max(_pathS, bestS);
            // A coral that slid back (a failed try, the robot backing off) is found again behind: kept at its old
            // progress, the target sat far up the intake ahead of it, it was never "in line", and the intake gave up
            // on it until intake was pressed again.
            if (best > 0.05f * 0.05f)
            {
                float back = 0f, bestBack = best, sBack = _pathS;
                for (var i = 0; i < _pathPts.Count - 1; i++)
                {
                    var a = _pathPts[i];
                    var seg = _pathPts[i + 1] - a;
                    var len = seg.magnitude;
                    if (back <= _pathS && len > 1e-5f)
                    {
                        var t = Mathf.Clamp01(Vector3.Dot(pos - a, seg) / (len * len));
                        var d = (a + seg * t - pos).sqrMagnitude;
                        if (d < bestBack) { bestBack = d; sBack = back + t * len; }
                    }
                    back += len;
                }
                if (bestBack < best) { best = bestBack; _pathS = sBack; }
            }

            var s = Mathf.Min(_pathS + coralPathLead, run);
            run = 0f;
            for (var i = 0; i < _pathPts.Count - 1; i++)
            {
                var len = (_pathPts[i + 1] - _pathPts[i]).magnitude;
                if (s <= run + len || i == _pathPts.Count - 2)
                {
                    var t = len > 1e-5f ? Mathf.Clamp01((s - run) / len) : 1f;
                    // Only the coral's long axis is steered (a tube looks the same rolled round it): the target turns
                    // the coral the shortest way from where it lies, so a coral picked up at any angle swings round
                    // as it is drawn in instead of spinning to a fixed pose.
                    var want = Quaternion.Slerp(_pathRots[i], _pathRots[i + 1], t) * coralPathAxis;
                    var have = piece.transform.rotation * _coralLocalAxis;
                    if (Vector3.Dot(want, have) < 0) want = -want;
                    // (from well off square until nearly square: let back into the rollers and belts part way round, a contact
                    // kicked the long tube the rest of the way in one step)
                    var off = Vector3.Angle(want, have);
                    _coralTurning = _coralTurning ? off > 8f : off > 50f;
                    var turning = _coralTurning;
                    // No progress up the channel for a moment (wedged against the flap, or kicked back by a roller): it is past
                    // the rollers' part by now, so it goes on through rather than sit there and never reach the claw.
                    if (turning || piece != _stallPiece || _pathS > _stallBestS + 0.01f)
                    {
                        _stallPiece = piece;
                        _stallBestS = _pathS;
                        _stallAt = Time.time;
                    }
                    CoralTurnsClearOfRobot(piece.transform, turning || Time.time - _stallAt > coralStallTime);
                    var touch = CoralTouchesIntake(piece.transform, have, out var onRollers);
                    if (touch) _intakeTouchAt = Time.time;
                    if (onRollers) _rollerTouchAt = Time.time;
                    // and only while it is in line with the way in: one knocked off it (or left behind as the robot
                    // drives off) is let go, not flown across to the claw. At the mouth it may swing about one end as
                    // it squares up (20 cm; at 30 it was flung across the mouth); inside the channel it is on the path (12 cm: at 6, squeezed under the flap it was let go instead of pulled).
                    var inLine = Mathf.Sqrt(best) < (i <= 1 ? 0.2f : 0.12f);
                    var held = inLine;
                    // (only its pull eases off: a target on the coral itself would count as caught at once)
                    var at = Vector3.Lerp(_pathPts[i], _pathPts[i + 1], t);
                    // Short of the claw the target never comes within the catch radius of the coral, however fast the rollers
                    // bring it (where the path doubles back it can): caught early, it jumped the rest of the way to the claw.
                    // Nor is it caught until it lies square to the claw: the catch would turn it the rest of the way in one step.
                    var unsquare = off > coralCatchSquare;
                    if ((i < _pathPts.Count - 2 || unsquare) && (at - pos).magnitude < coralCatchClear)
                        at = pos + ((at - pos).sqrMagnitude > 1e-8f ? (at - pos).normalized : (_pathPts[i + 1] - _pathPts[i]).normalized) * coralCatchClear;
                    if (unsquare && i >= _pathPts.Count - 2 && pulled) pulled.angularVelocity = Vector3.Cross(have, want).normalized * coralTurnSpeed;
                    // squares up at the mouth before it goes up: it would pass through the flap otherwise
                    if (turning)
                    {
                        at = pos + (_pathPts[1] - pos).normalized * coralCatchClear;
                        if (pulled) pulled.angularVelocity = Vector3.Cross(have, want).normalized * coralTurnSpeed;
                    }
                    // Not held for a moment: the intake lets it go (a target that kept following it held on to it for
                    // good, and no other coral could be taken); touched by the rollers again it is taken again.
                    if (held) _notHeldSince = float.NaN;
                    else if (float.IsNaN(_notHeldSince)) _notHeldSince = Time.time;
                    // (well out of range: it is dropped at once, and whatever the rollers touch next is taken up)
                    if (!held && Time.time - _notHeldSince > 0.3f) at = pos + Vector3.up * 2f;
                    coralCarrot.SetPositionAndRotation(at,
                        held ? Quaternion.FromToRotation(have, want) * piece.transform.rotation : piece.transform.rotation);
                    // eased into the claw over the last 10 cm, so it settles in instead of shooting past
                    var arrive = Mathf.Lerp(0.55f, 1f, Mathf.Clamp01((run + len - _pathS) / 0.10f));
                    groundCoralIntake.setForce(held ? coralPullForce * (turning ? 0.3f : 1f) * (i == _pathPts.Count - 2 ? arrive : 1f) : 0f);
                    break;
                }
                run += len;
            }
        }

        // The claw is solid to loose coral, but not to the one it is taking in: that one goes between its wheels.
        private void ClawLetsCoralIn(Transform piece)
        {
            if (piece == _clawLetsIn) return;
            foreach (var (a, b) in _clawLetsInPairs)
                if (a && b) Physics.IgnoreCollision(a, b, false);
            _clawLetsInPairs.Clear();
            _clawLetsIn = piece;
            if (!piece) return;
            var clawBody = clawEject.GetComponentInParent<Rigidbody>();
            var state = clawCoralState.stateTarget;
            var claw = clawBody.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && !c.transform.IsChildOf(state)).ToArray();
            foreach (var c in piece.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger))
                foreach (var w in claw)
                {
                    Physics.IgnoreCollision(c, w, true);
                    _clawLetsInPairs.Add((c, w));
                }
        }

        // A coral the rollers brought in end-on has no room to turn between the belts, the ramp and the frame: while it
        // squares up to the way up it passes the robot (only the robot: it still lies on the carpet and hits other coral).
        private void CoralTurnsClearOfRobot(Transform piece, bool turning)
        {
            if (turning && _turningCoral.Count == 0)
            {
                foreach (var c in piece.GetComponentsInChildren<Collider>())
                {
                    _turningCoral[c] = c.excludeLayers;
                    c.excludeLayers |= LayerMask.GetMask("Robot");
                }
            }
            else if (!turning && _turningCoral.Count > 0)
            {
                foreach (var kv in _turningCoral)
                    if (kv.Key)
                    {
                        kv.Key.excludeLayers = kv.Value;
                        // whatever it now overlaps it eases out of
                        if (kv.Key.attachedRigidbody) kv.Key.attachedRigidbody.maxDepenetrationVelocity = releasedDepenetration;
                    }
                _turningCoral.Clear();
            }
        }

        private void SelectSetpoint(bool hasCoral, bool groundMode)
        {
            if (CurrentSetpoint != ReefscapeSetpoints.Place)
            {
                _released = false;
                _prescored = false;
                _flingHold = float.NaN;
            }
            // Just caught at the station: the arm stays there a moment before it swings back
            if (!hasCoral) _hpHeldAt = float.NaN;
            else if (_target == hpIntake)
            {
                if (float.IsNaN(_hpHeldAt)) _hpHeldAt = Time.time;
                if (Time.time - _hpHeldAt < hpStowDelay) return;
            }
            // One piece at a time (the claw holds either): holding a coral it does not reach for algae, holding algae it
            // does not reach for coral - it stays stowed.
            var hasAlgae = _algaeController.HasPiece();
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    SetSetpoint(hasAlgae ? algaeHolding : stow, false); // 341: holding algae it goes to ALGAE_HOLDING, not STOW
                    break;
                case ReefscapeSetpoints.Intake:
                    if (CurrentRobotMode == ReefscapeRobotMode.Algae)
                        SetSetpoint(hasCoral ? stow : groundAlgae, false);
                    else
                        SetSetpoint(hasAlgae ? algaeHolding : hasCoral ? stow : groundMode ? groundIntake : hpIntake, false);
                    break;
                case ReefscapeSetpoints.L1:
                    SetSetpoint(l1, false); // the trough only from the front, never over the intake
                    break;
                case ReefscapeSetpoints.L2:
                    SetSetpoint(l2, !FacingReef());
                    break;
                case ReefscapeSetpoints.L3:
                    SetSetpoint(l3, !FacingReef());
                    break;
                case ReefscapeSetpoints.L4:
                    SetSetpoint(l4, !FacingReef());
                    break;
                case ReefscapeSetpoints.Stack:
                    SetSetpoint(hasCoral ? stow : lollipop, false);
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    SetSetpoint(hasCoral ? stow : lowAlgae, !FacingReef());
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    SetSetpoint(hasCoral ? stow : highAlgae, !FacingReef());
                    break;
                case ReefscapeSetpoints.Processor:
                    SetSetpoint(processor, false);
                    break;
                case ReefscapeSetpoints.Barge:
                    SetSetpoint(flingStart, false);
                    break;
                case ReefscapeSetpoints.Climb:
                    SetSetpoint(climbPrep, false);
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetSetpoint(climbed, false);
                    break;
                case ReefscapeSetpoints.Place:
                    PlacePiece();
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    CurrentCoralStationMode.DropType =
                        CurrentCoralStationMode.DropType == DropType.Ground ? DropType.Station : DropType.Ground;
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }
        }

        // Reef algae sits in the middle of a face, between its two branch poles: while going for it, auto-align
        // (left or right) lines the claw up on the face centre instead of on a pole.
        private void UpdateAlignOffset()
        {
            if (!_align) return;
            // L1 only scores from the front (never over the intake), so no intake-side align then
            var l1 = CurrentSetpoint == ReefscapeSetpoints.L1 || (CurrentSetpoint == ReefscapeSetpoints.Place && LastSetpoint == ReefscapeSetpoints.L1);
            _align.enableBackwardsAlign = !l1;
            var offset = _alignOffset;
            if (_target == lowAlgae || _target == highAlgae)
            {
                var toCentre = AutoAlignLeftAction.IsPressed() ? 1f : AutoAlignRightAction.IsPressed() ? -1f : 0f;
                offset.x += toCentre * reefFaceHalfWidth * (_mirror ? -1f : 1f);
            }

            _align.offset = offset;
        }

        private void SetSetpoint(MissDaisySetpoint setpoint, bool mirror)
        {
            _target = setpoint;
            _mirror = mirror;
        }

        // ---- current pose, in setpoint terms ("+ shoulder = toward the front", metres)

        private float JointShoulder(float frontAngle) => (_mirror ? -1f : 1f) * frontAngle; // the joint's +X swings to the front
        private float Shoulder() => (_mirror ? -1f : 1f) * Mathf.DeltaAngle(0, shoulder.GetSingleAxisAngle(JointAxis.X));
        private float Elevator() => elevator.GetElevatorHeight() / InchesPerMeter;
        private float Wrist() => Mathf.DeltaAngle(0, wrist.GetSingleAxisAngle(JointAxis.Y));
        private float IntakeAngle() => Mathf.DeltaAngle(0, intakePivot.GetSingleAxisAngle(JointAxis.X));

        private bool AtPose(MissDaisySetpoint p, float tolDeg)
        {
            return _target == p && Mathf.Abs(Mathf.DeltaAngle(Shoulder(), p.shoulderAngle)) < tolDeg &&
                   Mathf.Abs(Elevator() - p.elevatorHeight) < 0.04f &&
                   Mathf.Abs(Mathf.DeltaAngle(Wrist(), p.wristAngle)) < 10f;
        }

        private bool IsScore(MissDaisySetpoint p) => p == l2Score || p == l3Score || p == l4Score;

        /// <summary>341's Superstructure.poseAdjust, in this robot's terms: hold some joints until others are clear.</summary>
        private void UpdateSetpoints()
        {
            var d = _target;
            var algae = _algaeController.HasPiece() || CurrentRobotMode == ReefscapeRobotMode.Algae;
            var piece = _coralController.HasPiece() || _algaeController.HasPiece();
            float sc = Shoulder(), ec = Elevator(), wc = Wrist(), ic = IntakeAngle();
            float s = d.shoulderAngle, e = d.elevatorHeight, w = d.wristAngle, i = d.intakeAngle;
            var flinging = d == flingEnd;
            // Scoring L2 over the intake side: the intake swings part way out so the coral clears it (L2_SCORE, !isFront).
            if (_mirror && !algae && (d == l2 || d == l2Score)) i = mirroredL2IntakeAngle;
            if (flinging && !float.IsNaN(_flingHold)) s = _flingHold;
            var ground = d == groundIntake;
            // a guard that holds a joint keeps it at its last command, not at where it was measured: holding the
            // measured angle let gravity walk it (the arm crept round while intake was held)
            float holdS = float.IsNaN(_lastS) ? sc : _lastS, holdW = float.IsNaN(_lastW) ? wc : _lastW, holdI = float.IsNaN(_lastI) ? ic : _lastI;

            // Swinging back in a long way: the intake stays put (and so does the elevator while the intake is down).
            if (Mathf.Abs(s) < Mathf.Abs(sc) && Mathf.Abs(sc) - Mathf.Abs(s) > 70f)
            {
                i = holdI;
                if (Mathf.Abs(ic) > 20f) e = float.NaN;
            }

            // The claw over the deployed intake (ground intake): the intake stays out until the arm has swung clear of
            // it, or they pass through each other (e.g. switching from coral to algae intake).
            // (only for a real move in: a hold on any upward error held the intake wherever it sagged to, and it crept
            // down ~7 deg at the floor pickup, the front roller onto the coral)
            if (ic < -20f && i - ic > 15f && sc < -60f) i = Mathf.Max(holdI, groundIntake.intakeAngle);

            // Coming in from far out with coral: the wrist waits until the arm is back past 75 degrees.
            if (!algae && Mathf.Abs(s) < Mathf.Abs(sc) && Mathf.Abs(sc) > 75f) w = holdW;

            // Holding a piece near vertical with the wrist turned: square the wrist up before swinging.
            if (piece && Mathf.Abs(sc) < 30f && Mathf.Abs(Mathf.DeltaAngle(wc, stow.wristAngle)) > 3f &&
                Mathf.Abs(Mathf.DeltaAngle(w, stow.wristAngle)) < 3f)
            {
                if (!algae) s = holdS;
                w = stow.wristAngle;
            }

            // Going up out of a low arm-out pose: the elevator waits for the arm. (Not L1: the arm comes out low on the way
            // to it, and the elevator froze short of its height once the arm had passed 70.)
            if (ec < e && !ground && Mathf.Abs(sc) > 70f && e < 0.3f && !algae && d != climbPrep && d != climbed && d != l1) e = float.NaN;

            // Dunk: the shoulder comes over first, then the elevator follows down. (Following it down early pulled the
            // coral onto the top of the branch before the arm was over it.)
            if (IsScore(d) && ec > e && Mathf.Abs(Mathf.Abs(sc) - Mathf.Abs(s)) > 2.5f) e = float.NaN;

            // L1: a coral on the lip with room behind it is shoved back by the held one. One that cannot go back (two already
            // in the trough) presses into it, and the elevator lifts until the held coral rides up over them.
            if (d != l1) _l1Lift = _l1LiftTo = 0f;
            else
            {
                // (on until it is clear of them by the margin, so a plain release falls on their far side)
                if (_coralController.HasPiece() && _coralController.controller && Mathf.Abs(Mathf.DeltaAngle(sc, s)) < 30f &&
                    CoralPressDepth(_coralController.controller.transform) > 0.004f)
                    _l1LiftTo = Mathf.Min(_l1Lift + l1LiftMargin, l1MaxLift);
                _l1Lift = Mathf.MoveTowards(_l1Lift, _l1LiftTo, l1LiftSpeed * Time.fixedDeltaTime);
                e += _l1Lift;
            }

            // The intake moves first when the arm is about to swing out over it.
            if (Mathf.Abs(i - ic) > 70f && Mathf.Abs(s) > Mathf.Abs(sc) && s < 0f) s = holdS;

            // Travelling up through the low zone: the arm stands up.
            if (!flinging && !ground && e - ec > 0.076f && ec < 0.55f) s = 0f;

            // Coming down with coral: stand the arm up first (scoring dunks and the low setpoints excepted).
            if (!algae && !IsScore(d) && d != l1 && d != groundAlgae && d != lollipop && d != processor && !ground &&
                ec - e > 0.05f) s = 0f;

            // Algae coming down: the elevator waits for the shoulder.
            if (algae && e < ec && Mathf.Abs(sc - s) > 5f) e = float.NaN;

            // Coral low and tucked in: the wrist stays square.
            if (!algae && !(ec > 0.5f || Mathf.Abs(sc) > 15f)) w = stow.wristAngle;

            elevator.SetTarget(ProfileElevator(e) * InchesPerMeter);
            _lastS = s;
            _lastW = w;
            _lastI = i;
            _shoulderTarget = JointShoulder(s);
            shoulder.SetTargetAngle(_shoulderTarget).withAxis(JointAxis.X).noWrap(180).useCustomStartingOffset(0);
            ProfileShoulder(); // the setpoint call just wrote the joint's velocity: hold it to the profile before physics
            // 0..180 = 341's -pi/2..pi/2 (coral scoring .. reef algae); never round through -90
            wrist.SetTargetAngle(w).withAxis(JointAxis.Y).noWrap(-90).useCustomStartingOffset(0);
            intakePivot.SetTargetAngle(i).withAxis(JointAxis.X).useCustomStartingOffset(0);
            // The climber swings up and over the top (0 stowed inward, 165 deployed out the front, 77 climbed - 341's
            // angles), never down through the floor.
            // Pushed against, the cage leans away from the flat harpoon, and a post held off at the hook plates cannot get
            // past its barb. So the pull first slides up to where harpoon and cage are square and waits there for a hook
            // still folded: the leaning cage swings back in and the post clicks past it.
            if (d != climbed) _climbedAt = Time.time;
            var climberAngle = d == climbed && _hookAngles.Any(a => a > 2f) && Time.time - _climbedAt < climbSeatTime ? climbSeatAngle : d.climberAngle;
            climber.SetTargetAngle(climberAngle).withAxis(JointAxis.X).noWrap(-90).useCustomStartingOffset(0);
        }

        // placing at L1 (the claw stays solid to the reef there: it only drops the coral onto the trough)
        private bool IsL1Place() => CurrentSetpoint == ReefscapeSetpoints.Place && LastSetpoint == ReefscapeSetpoints.L1;

        private bool IsAlgaeTarget()
        {
            return _target == groundAlgae || _target == lollipop || _target == lowAlgae || _target == highAlgae;
        }

        private void PlacePiece()
        {
            _placeFrom = LastSetpoint; // the base class keeps the setpoint before Place here

            if (_algaeController.HasPiece() && _algaeController.controller)
            {
                var t = _algaeController.controller.transform;
                if (_placeFrom == ReefscapeSetpoints.Barge)
                {
                    // Fling: swing up and over; let go on the way up so the algae carries the claw's motion.
                    SetSetpoint(flingEnd, false);
                    // let go near the top with the arm swinging up past vertical: up and back over the robot
                    if (Elevator() > flingEnd.elevatorHeight - flingReleaseBelowTop && Shoulder() < flingReleaseAngle && !_released)
                    {
                        var dir = Quaternion.AngleAxis(flingPitch, transform.right) * -transform.forward; // up and back (left-handed: +pitch about right lifts -forward)
                        var body = t.GetComponent<Rigidbody>();
                        _algaeController.ReleaseGamePieceWithForce(Vector3.zero);
                        
                        // launch speed relative to the robot: the robot's own motion carries into the throw
                        if (body) body.velocity = (_robotBody ? _robotBody.velocity : Vector3.zero) + dir * flingSpeed;
                        _released = true;
                        _flingHold = Shoulder(); // stop the swing so the arm does not bat the algae back
                        // and the braking claw does not push it on (the arm at full speed added ~1 m/s, over the net)
                        if (body) StartCoroutine(ClawLetsGo(body.GetComponentsInChildren<Collider>(), 0.3f));
                    }

                    return;
                }

                var strength = _placeFrom == ReefscapeSetpoints.Processor ? processorEjectForce : algaeEjectForce;
                _algaeController.ReleaseGamePieceWithForce(t.InverseTransformDirection(clawEject.forward) * strength);
                
                _released = true;
                return;
            }

            if (!_coralController.HasPiece() || !_coralController.controller) return;

            // L2-L4: dunk from the prescore to the score pose, let go once the shoulder is nearly there.
            var score = _placeFrom switch
            {
                ReefscapeSetpoints.L2 => l2Score,
                ReefscapeSetpoints.L3 => l3Score,
                ReefscapeSetpoints.L4 => l4Score,
                _ => null
            };
            if (score)
            {
                // Place pressed on the way up: finish the prescore first, as 341 does (prescore, then the dunk). Going
                // straight to the score pose the shoulder swings out low and the elevator carries the coral up into the
                // branch below.
                if (!_prescored)
                {
                    SetSetpoint(_placeFrom == ReefscapeSetpoints.L2 ? l2 : _placeFrom == ReefscapeSetpoints.L3 ? l3 : l4, _mirror);
                    if (!AtPose(_target, 6f)) return;
                    _prescored = true;
                    _shoulderSlowed = 0f;
                    _lastShoulder = Shoulder();
                }

                SetSetpoint(score, _mirror);
                // let go once the dunk has arrived (as 1778 and 694 do): at the score angle, or stopped within 341's
                // tolerance of it (pressed on the branch) - a coral let go mid-swing stops dead and looks snapped off
                var tol = _placeFrom == ReefscapeSetpoints.L4 ? l4ReleaseTolerance : scoreReleaseTolerance;
                var off = Mathf.Abs(Mathf.DeltaAngle(Shoulder(), score.shoulderAngle));
                var step = Mathf.Abs(Mathf.DeltaAngle(Shoulder(), _lastShoulder));
                _shoulderStill = step < 0.15f ? _shoulderStill + Time.fixedDeltaTime : 0f;
                // the dunk held back to under 40% of its pace: the coral has met the branch
                _shoulderSlowed = step < 0.4f * dunkCruise * Mathf.Rad2Deg * Time.fixedDeltaTime ? _shoulderSlowed + Time.fixedDeltaTime : 0f;
                _lastShoulder = Shoulder();
                // L2/L3 let go as soon as they are within it, still swinging, or once the coral meets the branch on the
                // way down (within 20 deg) - before the arm and elevator push down on it; the coral carries on with the
                // claw's motion. L4 at the angle or stopped near it.
                var fromL4 = _placeFrom == ReefscapeSetpoints.L4;
                var onBranch = !fromL4 && off <= 20f && (_shoulderSlowed >= 0.04f || HeldCoralTouchesField());
                if (!onBranch && (off > tol || (fromL4 && off > 1.5f && _shoulderStill < 0.1f))) return;
            }

            // L2-L4: the wheels turn slowly (4 V) and push the coral straight out of the claw's mouth, nothing along its
            // axis: the dunk has put the branch inside it, and the branch holds it while the claw pulls away. L1: faster
            // (7 V), out the side and down over the trough lip.
            var piece = _coralController.controller.transform;
            var side = _mirror ? -transform.forward : transform.forward;
            var toL1 = _placeFrom == ReefscapeSetpoints.L1;
            // L1: just let go with a gentle level push (never up). The pose holds the coral over the one on the lip, so
            // the push tips it down the far side: behind the first, and onto the pair when both places are taken.
            var ejectDir = toL1 ? side : clawEject.forward;
            HeldCoralPassesReef(false);
            HeldCoralHitsCoral(false);
            _releasedAt = Time.time;
            
            // where the held coral overlapped the reef, it eases out instead of being shot out at PhysX's 10 m/s
            var coralBody = piece.GetComponentInChildren<Rigidbody>();
            if (coralBody) coralBody.maxDepenetrationVelocity = releasedDepenetration;
            // let go wherever it is: a coral pushed off its seat in the claw (by the corals already in the trough) was
            // never "at target", so the claw never let go and the L1 stack stopped at one
            // Scored on a branch, the claw pulling away does not knock it off.
            if (score) _clawClearUntil = Time.time + 1.2f;
            else ClawLetsCoralIn(null);
            // Not at a scoring pose (outtake at stow): the rollers just let go and it sits where it is, no push at all.
            var plainDrop = !toL1 && !score;
            if (plainDrop) _coralController.ReleaseGamePieceWithForce(Vector3.zero);
            else
                _coralController.ReleaseGamePieceWithContinuedForce(piece.InverseTransformDirection(ejectDir) * coralEjectForce,
                    toL1 ? coralEjectTime : scoreEjectTime, toL1 ? coralEjectSpeed : scoreEjectSpeed, false);
            // robot parts the gripped coral sat inside (fitted boxes round the carriage and mast at stow) let it out
            // instead of shoving it through the robot; it still lands on everything else
            if (coralBody) StartCoroutine(ReleasedCoralClears(coralBody));
            // the release zeroes its velocity: it leaves with the claw's motion (let go mid-dunk, it would stop dead),
            // at most the dunk's own pace (the claw can jump as it comes off the branch, and threw it past it)
            if (coralBody) coralBody.velocity = plainDrop ? Vector3.zero : Vector3.ClampMagnitude(_clawVelocity, 0.5f);
            _released = true;
        }

        // A coral gripped in the claw is part of the arm's rigid body: dunking it onto a branch would kick the whole
        // robot through the arm's stiff drive. While it is held it passes the reef; the moment the claw lets go it is a
        // free body again and lands on the branch by itself.
        private void HeldCoralPassesReef(bool held)
        {
            if (held)
            {
                var coral = LayerMask.NameToLayer("Coral");
                foreach (var t in new[] { clawCoralState != null ? clawCoralState.stateTarget : null, coralCarrot })
                    if (t)
                        foreach (var c in t.GetComponentsInChildren<Collider>())
                            if (c.gameObject.layer == coral && !_passingReef.ContainsKey(c))
                            {
                                _passingReef[c] = c.excludeLayers;
                                c.excludeLayers |= LayerMask.GetMask("Default");
                            }
                return;
            }

            if (_passingReef.Count == 0) return;
            foreach (var kv in _passingReef)
                if (kv.Key) kv.Key.excludeLayers = kv.Value;
            _passingReef.Clear();
        }

        // A held coral is solid to other coral (at L1 it shoves one already lying on the lip back into the trough, making
        // room: two side by side, a third on top). Held, the coral's own body detects no collisions at all (the game
        // piece system switches it off), so a coral-shaped capsule on the claw stands in for it - touching coral only.
        private void HeldCoralHitsCoral(bool on)
        {
            var piece = on && _coralController.controller ? _coralController.controller.transform : null;
            if (!_heldCoralBody && piece && clawCoralState != null && clawCoralState.stateTarget)
            {
                var go = new GameObject("Held Coral Body") { layer = LayerMask.NameToLayer("Robot") };
                go.transform.SetParent(clawCoralState.stateTarget, false);
                _heldCoralBody = go.AddComponent<CapsuleCollider>();
                _heldCoralBody.direction = 2; // along its local z, turned onto the coral's axis below
                _heldCoralBody.radius = 0.057f;
                _heldCoralBody.height = 0.30f;
                _heldCoralBody.includeLayers = LayerMask.GetMask("Coral");
                _heldCoralBody.excludeLayers = ~LayerMask.GetMask("Coral");
                _heldCoralBody.layerOverridePriority = 100; // over the claw body's exclusion of Coral
            }
            if (!_heldCoralBody) return;
            _heldCoralBody.enabled = piece;
            // at L1 it is solid to the reef as well (the held coral there sat inside the reef's base); at L2-L4 it passes
            // the reef while the dunk puts the branch in it
            var atL1 = _target == l1 || IsL1Place();
            _heldCoralBody.includeLayers = atL1 ? LayerMask.GetMask("Coral", "Default", "Field") : LayerMask.GetMask("Coral");
            _heldCoralBody.excludeLayers = ~_heldCoralBody.includeLayers;
            if (!piece) return;
            if (piece.gameObject != _axisPiece) { _coralLocalAxis = CoralLocalAxis(piece); _axisPiece = piece.gameObject; }
            _heldCoralBody.transform.SetPositionAndRotation(piece.position, Quaternion.FromToRotation(Vector3.forward, piece.rotation * _coralLocalAxis));
        }

        // 1. The roller arms and the top-roller flap are sprung down (surgical tubing): a spring torque about each hinge
        // toward its rest pose, with damping, applied every physics step (the joints' own spring drive does nothing
        // here). They lift only when a coral pushes them.
        private void SpringHinges()
        {
            foreach (var (body, k, c, i) in new[] { (rollerArmsBody, armsSpring, armsDamping, 0), (intakeFlapBody, flapSpring, flapDamping, 1) })
            {
                if (!body) continue;
                var cj = body.GetComponent<ConfigurableJoint>();
                var par = cj ? cj.connectedBody : null;
                if (!par) continue;
                var rel = Quaternion.Inverse(par.rotation) * body.rotation;
                if (!_hingeRestSet[i]) { _hingeRest[i] = rel; _hingeRestSet[i] = true; }
                var axisW = body.transform.TransformDirection(cj.axis).normalized;
                (rel * Quaternion.Inverse(_hingeRest[i])).ToAngleAxis(out var ang, out var ax);
                if (ang > 180f) ang -= 360f;
                var signed = ang * Mathf.Deg2Rad * Mathf.Sign(Vector3.Dot(par.transform.TransformDirection(ax), axisW));
                var w = Vector3.Dot(body.angularVelocity - par.angularVelocity, axisW);
                var torque = axisW * (-k * signed - c * w);
                body.AddTorque(torque, ForceMode.Force);
                par.AddTorque(-torque, ForceMode.Force); // a spring between the two: it pushes back on what it is mounted to
            }
        }

        // 4. While a coral is placed, the claw passes the reef like the coral it holds: its wheels and plates meeting a
        // branch would push the arm and elevator up mid-dunk.
        private void ClawColliders()
        {
            if (_clawColliders == null)
                _clawColliders = clawEject.root.GetComponentsInChildren<Collider>(true)
                    .Where(c => !c.isTrigger && (c.transform.IsChildOf(wrist.transform) || c.transform.IsChildOf(shoulder.transform)) &&
                                c.gameObject.layer != LayerMask.NameToLayer("Coral"))
                    .Select(c => (c, (LayerMask)c.excludeLayers)).ToArray();
        }

        private void ClawPassesReef(bool passing)
        {
            ClawColliders();
            if (passing == _clawPassing) return;
            _clawPassing = passing;
            foreach (var (c, ex) in _clawColliders)
                if (c) c.excludeLayers = passing ? ex | LayerMask.GetMask("Default") : ex;
        }

        // The coral in the claw touching the field (up at L2/L3 that is a branch): a capsule along its axis, ~5 mm proud.
        private bool HeldCoralTouchesField()
        {
            var piece = _coralController.controller ? _coralController.controller.transform : null;
            if (!piece) return false;
            if (piece.gameObject != _axisPiece) { _coralLocalAxis = CoralLocalAxis(piece); _axisPiece = piece.gameObject; }
            var half = piece.rotation * _coralLocalAxis * 0.10f;
            return Physics.CheckCapsule(piece.position - half, piece.position + half, 0.057f + 0.005f,
                LayerMask.GetMask("Field"), QueryTriggerInteraction.Ignore);
        }

        private void FindIntakeContacts()
        {
            if (_intakeContacts != null) return;
            var names = new[] { "Front Roller", "Flap Belts", "Flap Top Roller", "Channel Floor" };
            _intakeContacts = GetComponentsInChildren<Collider>(true).Where(c => names.Any(n => c.name.StartsWith(n)) && !c.isTrigger).ToArray();
        }

        private void RestoreCentring(Rigidbody rb)
        {
            if (!_centringCoral.TryGetValue(rb, out var was)) return;
            for (var i = 0; i < was.cols.Length; i++)
                if (was.cols[i]) was.cols[i].excludeLayers = was.masks[i];
            _centringCoral.Remove(rb);
        }

        // Outside the front roller only the rollers' friction moves a coral, whichever way it lies. A second coral that gets
        // past the roller while the intake has the first is carried up behind it the same way.
        private void PullOtherCoralOnRollers()
        {
            FindIntakeContacts();
            _otherCoral.Clear();
            var first = groundCoralIntake.hasGamePiece && groundCoralIntake.GamePiece ? groundCoralIntake.GamePiece.GetComponentInChildren<Rigidbody>() : null;
            foreach (var c in _intakeContacts)
            {
                var n = Physics.OverlapBoxNonAlloc(c.bounds.center, c.bounds.extents + Vector3.one * 0.02f, _otherHits, Quaternion.identity,
                    LayerMask.GetMask("Coral"), QueryTriggerInteraction.Ignore);
                for (var k = 0; k < n; k++)
                {
                    var rb = _otherHits[k].attachedRigidbody;
                    if (rb && rb != first && !rb.isKinematic && rb.detectCollisions) _otherCoral.Add(rb);
                }
            }
            foreach (var rb in _centringCoral.Keys.Where(k => !_otherCoral.Contains(k) && k != first).ToList()) RestoreCentring(rb);
            // the one the intake has taken stays clear until the pull has it near the middle
            if (first && _centringCoral.ContainsKey(first) && Mathf.Abs(Vector3.Dot(first.worldCenterOfMass - coralPath[0].position, transform.right)) < 0.1f) RestoreCentring(first);
            foreach (var rb in _otherCoral)
            {
                // the closest point on the way up, whatever its offset along the roller
                var pos = rb.worldCenterOfMass;
                var lateral = Vector3.Dot(pos - coralPath[0].position, transform.right);
                pos -= transform.right * lateral;
                Vector3 onPath = coralPath[0].position, ahead = coralPath[0].position;
                var best = float.MaxValue;
                var last = true;
                for (var i = 0; i < coralPath.Length - 1; i++)
                {
                    var a = coralPath[i].position;
                    var seg = coralPath[i + 1].position - a;
                    var t = Mathf.Clamp01(Vector3.Dot(pos - a, seg) / seg.sqrMagnitude);
                    var d = (a + seg * t - pos).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    onPath = a + seg * t;
                    ahead = onPath + seg.normalized * 0.08f;
                    last = (i == coralPath.Length - 2 && t >= 1f) || (i == 0 && t <= 0f); // still outside the roller: friction's job
                }
                // A coral half off the end of the roller: the roller's grip locks it against sliding along the roller, and the
                // intake's end plate blocks its outer half. It is drawn along past them to where the roller and ramps take it.
                if (last && Mathf.Sqrt(best) < 0.2f)
                {
                    var far = Mathf.Abs(lateral);
                    if (far > 0.26f && !_centringCoral.ContainsKey(rb))
                    {
                        var cols = rb.GetComponentsInChildren<Collider>();
                        _centringCoral[rb] = (cols, cols.Select(c => c.excludeLayers).ToArray());
                        foreach (var c in cols) c.excludeLayers |= LayerMask.GetMask("Robot");
                    }
                    if (_centringCoral.ContainsKey(rb))
                    {
                        if (far < 0.2f) { RestoreCentring(rb); continue; }
                        var along = Vector3.Project(rb.velocity, transform.right);
                        rb.AddForce(Vector3.ClampMagnitude((-transform.right * Mathf.Sign(lateral) * coralCentreSpeed - along) * 0.5f / Time.fixedDeltaTime, 40f), ForceMode.Acceleration);
                    }
                    continue;
                }
                if (last || Mathf.Sqrt(best) > 0.12f) continue;
                var want = (ahead - pos).normalized * otherCoralSpeed;
                rb.AddForce(Vector3.ClampMagnitude((want - rb.velocity) * 0.5f / Time.fixedDeltaTime, 40f), ForceMode.Acceleration);
            }
        }

        // The coral (a 4.5" tube, ~12" long, taken as a capsule along its axis) within ~1 cm of the intake's rollers,
        // belts or channel floor.
        private bool CoralTouchesIntake(Transform piece, Vector3 axis, out bool onRollers)
        {
            onRollers = false;
            FindIntakeContacts();
            var touching = false;
            foreach (var c in _intakeContacts)
                for (var k = -2; k <= 2; k++)
                {
                    var on = piece.position + axis.normalized * (0.065f * k);
                    if ((c.ClosestPoint(on) - on).magnitude >= 0.057f + 0.02f) continue;
                    touching = true;
                    if (c.name != "Channel Floor") onRollers = true;
                }
            return touching;
        }

        // Another coral (lying on the trough) just ahead of the held one, within about a coral's width.
        // How far the held coral is pressed down into another coral under it (m): it grows when the one it pushes can no
        // longer move. (One scored on a branch above it does not count: lifting into that only presses harder.)
        private float CoralPressDepth(Transform piece)
        {
            var deepest = 0f;
            if (!piece) return deepest;
            var mine = piece.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
            var coralMask = LayerMask.GetMask("Coral");
            var own = piece.GetComponentInChildren<Rigidbody>(); // (held, its colliders sit on a holder under the claw: not "another coral")
            foreach (var c in mine)
                foreach (var o in Physics.OverlapBox(c.bounds.center, c.bounds.extents, Quaternion.identity, coralMask, QueryTriggerInteraction.Ignore))
                    if (!o.transform.IsChildOf(transform) && o.attachedRigidbody != own && o.bounds.center.y < piece.position.y && Physics.ComputePenetration(c, c.transform.position, c.transform.rotation,
                            o, o.transform.position, o.transform.rotation, out _, out var depth))
                        deepest = Mathf.Max(deepest, depth);
            return deepest;
        }


        private System.Collections.IEnumerator ReleasedCoralClears(Rigidbody coral)
        {
            var mine = coral.GetComponentsInChildren<Collider>().Where(c => c.enabled && !c.isTrigger).ToArray();
            var robotLayer = LayerMask.GetMask("Robot");
            var pairs = new System.Collections.Generic.List<(Collider a, Collider b)>();
            // (the claw is already clear of one it has just scored: ClawLetsCoralIn)
            var clawBody = Time.time < _clawClearUntil ? clawEject.GetComponentInParent<Rigidbody>() : null;
            foreach (var c in mine)
                foreach (var r in Physics.OverlapBox(c.bounds.center, c.bounds.extents, Quaternion.identity, robotLayer, QueryTriggerInteraction.Ignore))
                    if (r.transform.IsChildOf(transform) && (!clawBody || r.attachedRigidbody != clawBody) && Physics.ComputePenetration(c, c.transform.position, c.transform.rotation,
                            r, r.transform.position, r.transform.rotation, out _, out _))
                    {
                        Physics.IgnoreCollision(c, r, true);
                        pairs.Add((c, r));
                    }
            var until = Time.time + 2f;
            while (pairs.Count > 0 && Time.time < until)
            {
                yield return new WaitForFixedUpdate();
                for (var k = pairs.Count - 1; k >= 0; k--)
                {
                    Collider pa = pairs[k].a, pb = pairs[k].b;
                    if (pa && pb && Physics.ComputePenetration(pa, pa.transform.position, pa.transform.rotation,
                            pb, pb.transform.position, pb.transform.rotation, out _, out _)) continue;
                    if (pa && pb) Physics.IgnoreCollision(pa, pb, false);
                    pairs.RemoveAt(k);
                }
            }
            foreach (var pair in pairs)
                if (pair.a && pair.b) Physics.IgnoreCollision(pair.a, pair.b, false);
        }

        // As 694 v3 does: only while climbed, the body's centre of mass moves. The top of the cage comes in over the intake
        // side, so the weight goes there, under it (341's weight is on its intake and arm side), and the robot hangs clear of
        // the carpet instead of tipping down onto the harpoon-side bumper. Driving, it stays automatic.
        private void ShiftCentreOfMassForClimb()
        {
            if (!_robotBody) return;
            var climbedNow = CurrentSetpoint == ReefscapeSetpoints.Climbed;
            if (climbedNow == _comShifted) return;
            _comShifted = climbedNow;
            _robotBody.automaticCenterOfMass = !climbedNow;
            if (climbedNow) _robotBody.centerOfMass = climbedCentreOfMass;
        }

        private static void Loop(AudioSource source, bool play, float pitch)
        {
            if (!source || !source.clip) return;
            source.pitch = pitch;
            if (play && !source.isPlaying) source.Play();
            else if (!play && source.isPlaying) source.Stop();
        }

        private void PlayOnce(AudioClip clip)
        {
            if (_oneShotAudio && clip) _oneShotAudio.PlayOneShot(clip);
        }

        private System.Collections.IEnumerator ClawLetsGo(Collider[] piece, float time)
        {
            ClawColliders();
            void Ignore(bool on)
            {
                foreach (var p in piece)
                foreach (var (c, _) in _clawColliders)
                    if (p && c) Physics.IgnoreCollision(p, c, on);
            }
            Ignore(true);
            yield return new WaitForSeconds(time);
            Ignore(false);
        }

        private static Vector3 CoralLocalAxis(Transform piece)
        {
            var mf = piece.GetComponentInChildren<MeshFilter>();
            if (!mf) return Vector3.forward;
            var size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
            var local = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            return piece.InverseTransformDirection(mf.transform.TransformDirection(local)).normalized;
        }

        // 341's shoulder runs MotionMagic (cruise 2 rot/s, accel 9 rot/s^2): the PID's velocity is held to that
        // profile - ramped up at the acceleration limit and braked early enough to stop on the target (v <= sqrt(2 a e)).
        private void ProfileShoulder()
        {
            if (!_shoulderJoint) _shoulderJoint = shoulder.GetComponent<ConfigurableJoint>();
            if (!_shoulderJoint) return;
            // the PID's velocity about the joint's X axis, signed: speeding up, braking and turning round are all held
            // to the one acceleration (a magnitude-only limit let the arm reverse at full speed in a step)
            var pid = _shoulderJoint.targetAngularVelocity.x;
            // the shoulder never passes 180, so the plain difference is the way it goes
            var err = Mathf.Abs(Mathf.DeltaAngle(0, _shoulderTarget) - Mathf.DeltaAngle(0, shoulder.GetSingleAxisAngle(JointAxis.X))) * Mathf.Deg2Rad;
            var cruise = IsScore(_target) ? dunkCruise : shoulderCruise; // the dunk is slow and smooth
            // braking planned at 60% of the acceleration: the arm lags its command a little, and braking at the full
            // rate overshot the stop by 2-3 deg and rocked back (the fore-aft wobble)
            var want = Mathf.Sign(pid) * Mathf.Min(Mathf.Abs(pid), cruise, Mathf.Sqrt(2f * 0.6f * shoulderAccel * err));
            _shoulderVel = Mathf.MoveTowards(_shoulderVel, want, shoulderAccel * Time.fixedDeltaTime);
            _shoulderJoint.targetAngularVelocity = new Vector3(_shoulderVel, 0f, 0f);
        }

        // The elevator's target moves to the setpoint along a trapezoid (cruise, acceleration, braking to stop on it),
        // so the stiff elevator PID follows a smooth path instead of jumping.
        private float ProfileElevator(float goal)
        {
            var dt = Time.fixedDeltaTime;
            if (float.IsNaN(_elevCmd)) _elevCmd = Elevator();
            // a guard holding the elevator (NaN): stop smoothly where it is commanded, not where it was measured (holding
            // at the measured height let it creep up while the dunk pushed on it)
            if (float.IsNaN(goal)) goal = _elevCmd + Mathf.Sign(_elevVel) * _elevVel * _elevVel / (2f * elevatorAccel);
            // never below the bottom: a hold caught while coming down fast (after a barge fling, the algae guard) put the
            // stopping point under the robot, and the elevator dived there and came back up
            goal = Mathf.Max(goal, 0f);
            var err = goal - _elevCmd;
            var want = Mathf.Sign(err) * Mathf.Min(IsScore(_target) ? dunkElevatorCruise : elevatorCruise, Mathf.Sqrt(2f * elevatorAccel * Mathf.Abs(err)));
            _elevVel = Mathf.MoveTowards(_elevVel, want, elevatorAccel * dt);
            var next = _elevCmd + _elevVel * dt;
            if (Mathf.Sign(goal - next) != Mathf.Sign(err) || Mathf.Abs(err) < 1e-4f) { next = goal; _elevVel = 0f; }
            _elevCmd = next;
            return _elevCmd;
        }

        private static bool BaseGameManagerRunning()
        {
            return MoSimCore.BaseClasses.GameManagement.BaseGameManager.Instance.RobotState != RobotState.Disabled;
        }

        private bool FacingReef()
        {
            if (!_blueReef || !_redReef) return true;
            var reef = (transform.position - _blueReef.position).sqrMagnitude <
                       (transform.position - _redReef.position).sqrMagnitude
                ? _blueReef
                : _redReef;
            return Vector3.Dot(transform.forward, reef.position - transform.position) > 0f;
        }
    }
}
