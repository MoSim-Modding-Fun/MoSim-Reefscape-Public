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
using System.Linq;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.SPAM._180
{
    /// <summary>
    /// 180 S.P.A.M. Anemone: cascade elevator carrying a pivoting arm tube. Floor coral is picked up by the four-bar
    /// intake at the back, funnelled end-first by the indexer into the bottom of the tube and scored out of its top end
    /// on the front (scoring) side. Algae rides in the wheel "V" over the tube's top end. The climber arm folds out of
    /// the left side, hooks a deep cage post and swings back up.
    /// As in 180's RobotContainer: the arm waits at the receive pose while the intake is out, elevator and arm travel
    /// together to each preset, and the robot stows itself once a piece has left.
    /// </summary>
    public class Anemone : ReefscapeRobotBase
    {
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint arm, climber;
        [Tooltip("The intake's driven crank (on the crank shaft); the bottom bar rides on it, sprung, in its arc slots.")]
        [SerializeField] private GenericJoint intakeBar;
        [Tooltip("The head's roller colliders: they knock a standing (lollipop) coral over; a coral they have caught is let " +
                 "through them.")]
        [SerializeField] private Collider[] headRollerColliders;
        [Tooltip("The coral's long axis in its own frame (the floor intake only takes a coral lying down).")]
        [SerializeField] private Vector3 coralLongLocal = Vector3.up;
        [Tooltip("The chute's walls: a station coral the tube has caught is let through them until it is seated (it can " +
                 "drop in at an angle and wedge).")]
        [SerializeField] private Collider[] chuteWalls;
        [Tooltip("L1: as the coral slides out the end effector tips up this far (degrees, toward upright) for this long, " +
                 "flipping the coral over the trough's lip (as 180's L4 flip: the arm pivot up a little at the end).")]
        [SerializeField] private float l1FlipAngle = 35f;
        [SerializeField] private float l1FlipTime = 0.15f;
        [Tooltip("Where the coral's centre is (m past the tube's top lip) when the rollers let it go and the end effector tips up.")]
        [SerializeField] private float l1TipAt = 0.1f;
        [Tooltip("Intake and indexer wheels, spun for show.")]
        [SerializeField] private GenericAnimationJoint[] intakeRollerVisuals;
        [Tooltip("Rollers inside the arm tube, spun for show; positive moves a coral toward the top end.")]
        [SerializeField] private GenericAnimationJoint[] tubeRollerVisuals;
        [Tooltip("Algae wheels at the top of the arm, spun for show; positive pulls a ball in.")]
        [SerializeField] private GenericAnimationJoint[] algaeWheelVisuals;
        [Tooltip("Each wheel group's spin direction when its rollers run forward (set by the builder from where the piece goes).")]
        [SerializeField] private float[] intakeRollerSigns, tubeRollerSigns, algaeWheelSigns;
        [Tooltip("Arm tube angle in the model (deg above horizontal toward the front).")]
        [SerializeField] private float armCadAngle = 51.3f;
        [Tooltip("Sign that turns 'tube end up' into the arm joint's X rotation.")]
        [SerializeField] private float armSign = -1f;
        [Tooltip("Sign that turns 'intake folded up' into the driven bar's X rotation.")]
        [SerializeField] private float intakeSign = 1f;
        [Tooltip("Intake angle (deg folded up from deployed) the prefab rests at: stowed, inside the frame.")]
        [SerializeField] private float intakeRestAngle = 85f;
        [Tooltip("Sign that turns 'climber folded out' into the climber's Z rotation.")]
        [SerializeField] private float climberSign = 1f;

        [Header("PIDs")]
        [SerializeField] private PidConstants armPid;
        [SerializeField] private PidConstants intakePid, climberPid;
        [Tooltip("The climb itself: the winch hauls the cage round in about 4 s, " +
                 "deg/s of the arm's target. The target moves, not the speed limit: the drive's force scales with that limit, " +
                 "and a capped arm stalled just as the robot's weight came on.")]
        [SerializeField] private float climbPullRate = 40f;

        [Header("Setpoints")]
        [SerializeField] private AnemoneSetpoint stow;
        [SerializeField] private AnemoneSetpoint groundIntake, hpIntake;
        [SerializeField] private AnemoneSetpoint l1, l2, l3, l4;
        [SerializeField] private AnemoneSetpoint groundAlgae, lollipop, lowAlgae, highAlgae, processor, net;
        [Tooltip("Barge: the ball carried low (~1 m) on top of the end effector while the robot drives up; Place " +
                 "then goes up to Net.")]
        [SerializeField] private AnemoneSetpoint netApproach;
        [SerializeField] private AnemoneSetpoint climbPrep, climbed;

        [Header("Intakes")]
        [SerializeField] private ReefscapeGamePieceIntake groundCoralIntake;
        [SerializeField] private ReefscapeGamePieceIntake hpCoralIntake, algaeIntake;

        [Header("Game Piece States")]
        [Tooltip("A floor coral's way up through the robot, in order: just inside the front roller, up between the head " +
                 "rollers, in the indexer, end-first under the tube's bellmouth. It steps on at each, and into the tube " +
                 "(coralState) only while the arm waits at the receive pose.")]
        [SerializeField] private GamePieceState[] floorCoralStates;
        [Tooltip("Held in the tube (floor coral's last step, the station's only one).")]
        [SerializeField] private GamePieceState coralState;
        [SerializeField] private GamePieceState algaeState;

        [Header("Audio")]
        [Tooltip("Looped while any rollers run (intake, indexer, tube, algae wheels).")]
        [SerializeField] private AudioClip rollerClip;
        [Tooltip("Looped while the algae wheels hold a ball (stalled on it).")]
        [SerializeField] private AudioClip algaeStallClip;
        [Tooltip("Once, as a coral seats in the tube.")]
        [SerializeField] private AudioClip coralSeatClip;
        [Tooltip("Once, as the hook takes the cage.")]
        [SerializeField] private AudioClip cageHookClip;

        [Header("Tuning")]
        [Tooltip("Seconds the rollers keep pushing after the coral's nose has met something.")]
        [SerializeField] private float coralContactPushTime = 0.35f;
        [Tooltip("Forward points out of the tube's top end: coral leaves this way.")]
        [SerializeField] private Transform coralEject;
        [Tooltip("Forward points out of the algae wheels: algae leaves this way.")]
        [SerializeField] private Transform algaeEject;
        [Tooltip("Visual spin (deg/s): slow enough that a spoked wheel visibly turns at 30-60 fps instead of strobing.")]
        [SerializeField] private float rollerVisualSpeed = 360f;
        [Tooltip("L2/L3: the coral leaves the tube ~12 cm behind the bumper and flies ~0.3 m onto the branch.")]
        [SerializeField] private float coralEjectSpeed = 3f;
        [Tooltip("L1 roller push-out (180 runs them at 0.3): slow, so the coral tips over the trough rather than shooting.")]
        [SerializeField] private float l1EjectSpeed = 1.8f;
        [Tooltip("L4 eject speed (2 m/s glances off the post sideways).")]
        [SerializeField] private float l4EjectSpeed = 1.8f;
        [SerializeField] private float algaeEjectForce = 6f;
        [SerializeField] private float processorEjectForce = 3f;
        [Tooltip("Net shot (180's code, and Einstein R3 M9 / the reveal video): the elevator goes to NET as the arm swings " +
                 "the ball up on top; the driver waits there, then the wheels push the ball out over the net in a small " +
                 "lob that lands 0.5-1.5 m out. In the footage the ball sits 0.3-0.5 m above the rim at the top; MoSim's " +
                 "barge rim is level with it, so the lob is steeper here to clear the rim. Speed (m/s, on top of the " +
                 "robot's motion) and angle above horizontal.")]
        [SerializeField] private float netSpeed = 2.75f;
        [SerializeField] private float netPitch = 55f;
        [Tooltip("The wheels spit once the elevator is this close to Net (m) and the arm this close (deg): the arm is still swinging.")]
        [SerializeField] private float netReleaseElevator = 0.06f;
        [SerializeField] private float netReleaseArm = 15f;
        [Tooltip("Seconds after a piece leaves before the robot stows itself.")]
        [SerializeField] private float stowAfterScore = 0.6f;
        [Tooltip("Inches from a reef branch pole to the face centre, where the algae sits.")]
        [SerializeField] private float reefFaceHalfWidth = 6.47f;


        private const float InchesPerMeter = 39.3700787f;
        private bool _intakeLetGo, _pressedAgainForAlgae;

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode
            _coralController, _algaeController;

        private AnemoneSetpoint _target;
        private bool _released;
        private const float CoralHalfLength = 0.15f;
        private bool _l1Tipping, _coralLeaving;
        private float _l1Tip;
        private float _climberTarget;
        private RobotFramework.Controllers.Drivetrain.DriveController _drive;
        private float _driveTurn = 1f;
        private float _driveScale = 1f;
        private float _releasedAt;
        private ReefscapeSetpoints _placeFrom;
        private ReefscapeAutoAlign _align;
        private Vector3 _alignOffset;
        private Rigidbody _robotBody;
        private float _stationDropDistance;

        protected override void Start()
        {
            base.Start();

            arm.SetPid(armPid);
            intakeBar.SetPid(intakePid);
            climber.SetPid(climberPid);

            _target = stow;

            RobotGamePieceController.SetPreload(coralState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
            _algaeController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));

            var states = new GamePieceState[floorCoralStates.Length + 1];
            floorCoralStates.CopyTo(states, 0);
            states[^1] = coralState;
            _coralController.gamePieceStates = states;
            _coralController.intakes.Add(groundCoralIntake);
            _coralController.intakes.Add(hpCoralIntake);

            _algaeController.gamePieceStates = new[] { algaeState };
            _algaeController.intakes.Add(algaeIntake);

            _align = GetComponent<ReefscapeAutoAlign>();
            if (_align) _alignOffset = _align.offset;
            _robotBody = GetComponent<Rigidbody>();
            _drive = GetComponent<RobotFramework.Controllers.Drivetrain.DriveController>();
            if (_drive) _driveTurn = _drive.rotationSpeedMultiplier;
            _stationDropDistance = CurrentCoralStationMode.DropDistance;
            _climbScorer = GetComponent<Games.Reefscape.Scoring.Scorers.ClimbScorer>();

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
            _oneShotAudio = Source(null, false, 0.6f);
        }

        private AudioSource _rollerAudio, _stallAudio, _oneShotAudio;
        private Games.Reefscape.Scoring.Scorers.ClimbScorer _climbScorer;
        private bool _wasSeated, _wasHooked;

        private static void Loop(AudioSource a, bool on)
        {
            if (!a || !a.clip) return;
            if (on && !a.isPlaying) a.Play();
            else if (!on && a.isPlaying) a.Stop();
        }

        private void PlaySounds(bool running, bool rollersOn, bool hasAlgae, bool inTube)
        {
            Loop(_rollerAudio, running && rollersOn);
            Loop(_stallAudio, running && hasAlgae);
            if (inTube && !_wasSeated && running && coralSeatClip) _oneShotAudio.PlayOneShot(coralSeatClip);
            _wasSeated = inTube;
            var hooked = _climbScorer && CurrentSetpoint is ReefscapeSetpoints.Climb or ReefscapeSetpoints.Climbed && _climbScorer.ScoringTriggered;
            if (hooked && !_wasHooked && cageHookClip) _oneShotAudio.PlayOneShot(cageHookClip);
            _wasHooked = hooked;
        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPid);
            intakeBar.UpdatePid(intakePid);
            climber.UpdatePid(climberPid);
        }

        private void FixedUpdate()
        {
            var hasCoral = _coralController.HasPiece();
            var hasAlgae = _algaeController.HasPiece();
            var intaking = IntakeAction.IsPressed();
            var groundMode = CurrentCoralStationMode.DropType == DropType.Ground;
            var coralIntaking = intaking && CurrentSetpoint == ReefscapeSetpoints.Intake &&
                                CurrentRobotMode != ReefscapeRobotMode.Algae;
            var groundIntaking = coralIntaking && groundMode && !hasCoral;
            var hpIntaking = coralIntaking && !groundMode && !hasCoral;

            var state = _coralController.GetCurrentState();
            var inTube = hasCoral && _coralController.atTarget && state != null && state.name == coralState.name;
            // A floor coral on its way up the indexer holds the robot where it is until it is in the tube.
            var coralInTransit = hasCoral && !inTube;
            // the algae intake needs a fresh press once a coral is in: the button is usually still held as it arrives
            if (!hasCoral) _pressedAgainForAlgae = false;
            else if (!intaking) _intakeLetGo = true;
            if (hasCoral && _intakeLetGo && intaking) _pressedAgainForAlgae = true;
            if (!hasCoral) _intakeLetGo = false;
            if (!coralInTransit) SelectSetpoint(groundMode);

            var floorIntakeRunning = groundIntaking && AtPose(groundIntake, 6f);
            // the human player only drops once the tube is up at the chute (the station drops within this distance)
            CurrentCoralStationMode.DropDistance = hpIntaking && AtPose(hpIntake, 4f) ? _stationDropDistance : 0f;
            // only a coral lying on the carpet is taken; a standing one is knocked over by the rollers first
            _coralController.RequestIntake(groundCoralIntake, floorIntakeRunning && (groundCoralIntake.hasGamePiece || FlatCoralAtRoller()));
            LetCaughtCoralThrough();
            LetCaughtAlgaeIn();
            LetStationCoralIn();
            _coralController.RequestIntake(hpCoralIntake, hpIntaking && AtPose(hpIntake, 6f));
            _algaeController.RequestIntake(algaeIntake, intaking && IsAlgaeTarget() && !hasAlgae);
            StepCoral(hasCoral, groundMode);
            _algaeController.SetTargetState(algaeState);

            var running = BaseGameManagerRunning();
            var rollers = running && (floorIntakeRunning || coralInTransit) ? 1f : 0f;
            Spin(intakeRollerVisuals, intakeRollerSigns, rollers);
            var placing = CurrentSetpoint == ReefscapeSetpoints.Place && _released;
            var tube = running ? (floorIntakeRunning || coralInTransit ? 1f : hpIntaking ? -1f : placing && !AlgaeShot() ? 1f : 0f) : 0f;
            Spin(tubeRollerVisuals, tubeRollerSigns, tube);
            var algaeIn = intaking && IsAlgaeTarget() && !hasAlgae;
            var wheels = running ? (algaeIn ? 1f : placing && _placeFrom is ReefscapeSetpoints.Barge or ReefscapeSetpoints.Processor ? -1f : 0f) : 0f;
            Spin(algaeWheelVisuals, algaeWheelSigns, wheels);
            PlaySounds(running, rollers != 0 || tube != 0 || wheels != 0, hasAlgae, inTube);

            // 180's coralScoreEnd: once the piece is away, back to stow.
            if (_released && !(AlgaeShot() ? hasAlgae : hasCoral) && Time.time - _releasedAt > stowAfterScore && !_coralLeaving &&
                CurrentSetpoint == ReefscapeSetpoints.Place)
                SetState(ReefscapeSetpoints.Stow);

            UpdateAlignOffset();
            // pulling on the cage: no driving (a push leans the cage over and walks the upright out of the hook)
            var driveScale = CurrentSetpoint == ReefscapeSetpoints.Climbed ? 0f : 1f;
            if (_drive && !Mathf.Approximately(driveScale, _driveScale))
            {
                // SetDriveMp, not moveSpeedMultiplier: that one is only read in robot-relative drive
                _driveScale = driveScale;
                _drive.SetDriveMp(driveScale);
                _drive.rotationSpeedMultiplier = _driveTurn * driveScale;
            }
            UpdateSetpoints(floorIntakeRunning || coralInTransit);
        }

        // +1 runs a group's wheels the way that moves its piece forward (in, up the tube, into the wheels' V)
        private void Spin(GenericAnimationJoint[] wheels, float[] signs, float run)
        {
            for (var i = 0; i < wheels.Length; i++)
                if (wheels[i]) wheels[i].VelocityRoller(run * rollerVisualSpeed * (signs != null && i < signs.Length ? signs[i] : 1f));
        }

        private void SelectSetpoint(bool groundMode)
        {
            if (CurrentSetpoint != ReefscapeSetpoints.Place)
            {
                _released = false;
            }
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    _target = stow;
                    break;
                case ReefscapeSetpoints.Intake:
                    // with a coral in the tube, intake again takes an algae into the wheels (180 carries both)
                    if (CurrentRobotMode == ReefscapeRobotMode.Algae)
                        _target = _algaeController.HasPiece() ? stow : groundAlgae;
                    else if (_coralController.HasPiece())
                        _target = _pressedAgainForAlgae && !_algaeController.HasPiece() ? groundAlgae : stow;
                    else
                        _target = groundMode ? groundIntake : hpIntake;
                    break;
                case ReefscapeSetpoints.L1:
                    _target = l1;
                    break;
                case ReefscapeSetpoints.L2:
                    _target = l2;
                    break;
                case ReefscapeSetpoints.L3:
                    _target = l3;
                    break;
                case ReefscapeSetpoints.L4:
                    _target = l4;
                    break;
                case ReefscapeSetpoints.Stack:
                    _target = lollipop;
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    _target = lowAlgae;
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    _target = highAlgae;
                    break;
                case ReefscapeSetpoints.Processor:
                    _target = processor;
                    break;
                case ReefscapeSetpoints.Barge:
                    _target = netApproach ? netApproach : net;
                    break;
                case ReefscapeSetpoints.Climb:
                    _target = climbPrep;
                    break;
                case ReefscapeSetpoints.Climbed:
                    _target = climbed;
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
        // (left or right) lines the algae wheels up on the face centre instead of on a pole.
        private void UpdateAlignOffset()
        {
            if (!_align) return;
            var offset = _alignOffset;
            if (_target == lowAlgae || _target == highAlgae)
            {
                var toCentre = AutoAlignLeftAction.IsPressed() ? 1f : AutoAlignRightAction.IsPressed() ? -1f : 0f;
                offset.x += toCentre * reefFaceHalfWidth;
                // the wheels are off the robot's centre: the ball goes in front of them
                offset.x -= transform.InverseTransformPoint(algaeState.stateTarget.position).x * InchesPerMeter;
            }

            _align.offset = offset;
        }

        // ---- the floor catch

        private GameObject _passing;

        private bool FlatCoralAtRoller()
        {
            var b = groundCoralIntake.GetComponent<BoxCollider>();
            if (!b) return true;
            var hits = Physics.OverlapBox(b.transform.TransformPoint(b.center), Vector3.Scale(b.size, b.transform.lossyScale) / 2 + Vector3.one * 0.02f,
                b.transform.rotation, LayerMask.GetMask("Coral"), QueryTriggerInteraction.Collide);
            foreach (var c in hits)
            {
                var piece = c.attachedRigidbody ? c.attachedRigidbody.transform : c.transform.root;
                var along = piece.TransformDirection(coralLongLocal);
                if (Mathf.Abs(Vector3.Dot(along, Vector3.up)) < 0.5f && piece.position.y - transform.position.y < 0.15f) return true;
            }
            return false;
        }

        // the rollers are solid to coral, but the one they have caught is pulled in between them
        private void LetCaughtCoralThrough()
        {
            if (headRollerColliders == null || headRollerColliders.Length == 0) return;
            var caught = groundCoralIntake.hasGamePiece ? groundCoralIntake.GamePiece : null;
            if (_passing && _passing != caught && !(_coralController.HasPiece() && _coralController.controller && _coralController.controller.gameObject == _passing))
            {
                SetPassing(_passing, false);
                _passing = null;
            }
            if (caught && caught != _passing)
            {
                SetPassing(caught, true);
                _passing = caught;
            }
        }

        private void SetPassing(GameObject piece, bool pass) => SetPassing(piece, pass, headRollerColliders);

        private GameObject _algaePassing;
        private Collider[] _algaeWheelColliders;

        // A ball a wheel has caught rolls round that wheel to its seat: the wheels give (they are compliant), so it passes
        // them on the way; everything else on the arm still stops it. Seated, it touches none of them.
        private void LetCaughtAlgaeIn()
        {
            _algaeWheelColliders ??= arm.GetComponentsInChildren<Collider>(true).Where(c => c.name.StartsWith("Algae Shaft") && c.name.EndsWith(" Wheel")).ToArray();
            var caught = algaeIntake.hasGamePiece ? algaeIntake.GamePiece : null;
            if (_algaePassing && _algaePassing != caught)
            {
                SetPassing(_algaePassing, false, _algaeWheelColliders);
                _algaePassing = null;
            }
            if (caught && caught != _algaePassing)
            {
                SetPassing(caught, true, _algaeWheelColliders);
                _algaePassing = caught;
            }
        }

        private static void SetPassing(GameObject piece, bool pass, Collider[] others)
        {
            if (others == null) return;
            foreach (var c in piece.GetComponentsInChildren<Collider>(true))
            foreach (var r in others)
                if (r) Physics.IgnoreCollision(c, r, pass);
        }

        private System.Collections.IEnumerator PassChuteBriefly(GameObject piece)
        {
            SetPassing(piece, true, chuteWalls);
            yield return new WaitForSeconds(1f);
            if (piece && !(_coralController.HasPiece() && _coralController.controller && _coralController.controller.gameObject == piece))
                SetPassing(piece, false, chuteWalls);
        }

        private GameObject _stationPassing;

        // a station coral caught in the top of the tube is pulled to its seat through the walls; once seated it meets them again
        private void LetStationCoralIn()
        {
            var caught = hpCoralIntake.hasGamePiece ? hpCoralIntake.GamePiece : null;
            var seated = _coralController.HasPiece() && _coralController.atTarget;
            if (_stationPassing && (seated || _stationPassing != caught))
            {
                SetPassing(_stationPassing, false, chuteWalls);
                _stationPassing = null;
            }
            if (caught && !seated && caught != _stationPassing)
            {
                SetPassing(caught, true, chuteWalls);
                _stationPassing = caught;
            }
        }

        // ---- current pose, in setpoint terms

        private float ArmAngle() => armCadAngle + armSign * Mathf.DeltaAngle(0, arm.GetSingleAxisAngle(JointAxis.X));
        private float Elevator() => elevator.GetElevatorHeight() / InchesPerMeter;
        private float IntakeAngle() => intakeRestAngle + intakeSign * Mathf.DeltaAngle(0, intakeBar.GetSingleAxisAngle(JointAxis.X));

        private bool AtPose(AnemoneSetpoint p, float tolDeg)
        {
            return _target == p && Mathf.Abs(ArmAngle() - p.armAngle) < tolDeg &&
                   Mathf.Abs(Elevator() - p.elevatorHeight) < 0.03f &&
                   Mathf.Abs(IntakeAngle() - p.intakeAngle) < 8f;
        }

        private void UpdateSetpoints(bool feeding)
        {
            var d = _target;
            float e = d.elevatorHeight, a = d.armAngle, i = d.intakeAngle;
            // The intake comes in before the arm swings low over it, and stays out while a coral is on its way up.
            if (feeding)
            {
                e = groundIntake.elevatorHeight;
                a = groundIntake.armAngle;
                i = groundIntake.intakeAngle;
            }

            elevator.SetTarget(e * InchesPerMeter);
            // the arm never swings through straight down (the elevator is in the way)
            // L1: tip up once the coral is mostly out (its tail still on the lip), to flip it into the trough
            _l1Tip = Mathf.MoveTowards(_l1Tip, _l1Tipping ? l1FlipAngle : 0f, l1FlipAngle / l1FlipTime * Time.fixedDeltaTime);
            a += _l1Tip;
            arm.SetTargetAngle(armSign * (a - armCadAngle)).withAxis(JointAxis.X)
                .noWrap(armSign * (-90f - armCadAngle)).useCustomStartingOffset(0);
            intakeBar.SetTargetAngle(intakeSign * (i - intakeRestAngle)).withAxis(JointAxis.X).useCustomStartingOffset(0);
            // out past flat, then back up through upright and over the chassis: never round through pointing down
            _climberTarget = CurrentSetpoint == ReefscapeSetpoints.Climbed
                ? Mathf.MoveTowards(_climberTarget, d.climberAngle, climbPullRate * Time.fixedDeltaTime)
                : d.climberAngle;
            climber.SetTargetAngle(climberSign * _climberTarget).withAxis(JointAxis.Z)
                .noWrap(climberSign * 180f).useCustomStartingOffset(0);
        }

        // Place lets go of the algae from the barge and processor setpoints; from a reef level it scores the coral
        // (180 holds both: the coral in the tube, the algae in the wheels over its top end).
        private bool AlgaeShot() => _placeFrom is ReefscapeSetpoints.Barge or ReefscapeSetpoints.Processor;

        private bool IsAlgaeTarget()
        {
            return _target == groundAlgae || _target == lollipop || _target == lowAlgae || _target == highAlgae;
        }

        private void PlacePiece()
        {
            if (!_released) _placeFrom = LastSetpoint; // the base class keeps the setpoint before Place here

            if (_algaeController.HasPiece() && _algaeController.controller && !_released &&
                (AlgaeShot() || !_coralController.HasPiece()))
            {
                var t = _algaeController.controller.transform;
                var body = t.GetComponent<Rigidbody>();
                if (_placeFrom == ReefscapeSetpoints.Barge)
                {
                    // Place: up to NET as the arm swings the ball over the top, a moment there, then the wheels spit it
                    // (180's netScore: the wheels only run once the elevator is at NET)
                    _target = net;
                    if (Mathf.Abs(Elevator() - net.elevatorHeight) > netReleaseElevator || Mathf.Abs(ArmAngle() - net.armAngle) > netReleaseArm) return;
                    var dir = Quaternion.AngleAxis(-netPitch, transform.right) * transform.forward;
                    if (!_algaeController.ReleaseGamePieceWithForce(Vector3.zero)) return;
                    // thrown toward the net (the robot's front), on top of the robot's own motion
                    if (body) body.velocity = RobotVelocityAt(body.position) + dir * netSpeed;
                }
                else
                {
                    var strength = _placeFrom == ReefscapeSetpoints.Processor ? processorEjectForce : algaeEjectForce;
                    if (!_algaeController.ReleaseGamePieceWithForce(t.InverseTransformDirection(algaeEject.forward) * strength)) return;
                    // the wheels' push is on top of the robot's own motion (the release starts the ball from rest)
                    if (body) body.velocity += RobotVelocityAt(body.position);
                }

                _released = true;
                _releasedAt = Time.time;
                return;
            }

            if (_released || !_coralController.HasPiece() || !_coralController.controller) return;

            // The tube rollers push the coral straight out of the top end, onto the branch or into the trough.
            var piece = _coralController.controller.transform;
            var l1Score = _placeFrom == ReefscapeSetpoints.L1;
            var coralBody = piece.GetComponent<Rigidbody>();
            if (!_coralController.ReleaseGamePieceWithForce(Vector3.zero)) return;
            _coralLeaving = true;
            StartCoroutine(PushCoralOut(coralBody, l1Score ? l1EjectSpeed : _placeFrom == ReefscapeSetpoints.L4 ? l4EjectSpeed : coralEjectSpeed, l1Score));
            _released = true;
            _releasedAt = Time.time;
        }

        // The robot's velocity at a point (its drive and its turning): a piece let go starts with it.
        private Vector3 RobotVelocityAt(Vector3 point) => _robotBody ? _robotBody.GetPointVelocity(point) : Vector3.zero;

        // The tube rollers carry the coral out of the top end at this speed. While it touches nothing it stays on the tube's
        // axis and turns with the arm. Its nose meeting something (a branch, the trough) ends the carry: from there it is
        // physics, the tube's walls under its tail and the rollers still pushing. Once its tail is out it flies on with the
        // tube's motion at that point, so the tip-up's flick and the robot's drive carry over.
        private System.Collections.IEnumerator PushCoralOut(Rigidbody body, float speed, bool l1)
        {
            if (!body) { _coralLeaving = false; yield break; }
            var armBody = arm.GetComponent<Rigidbody>();
            var relRot = Quaternion.Inverse(arm.transform.rotation) * body.rotation;
            var past = Vector3.Dot(body.position - coralEject.position, coralEject.forward); // its centre past the top lip
            var tipEnd = -1f;
            void Tip()
            {
                // L1: the rollers let the coral go part way out and the end effector tips up: the tube's floor rises under
                // its tail and flips it, nose down, into the trough
                if (!l1 || tipEnd >= 0 || past < l1TipAt) return;
                _l1Tipping = true;
                tipEnd = Time.time + l1FlipTime;
            }
            bool Touches(Vector3 dir)
            {
                foreach (var hit in body.SweepTestAll(dir, speed * Time.fixedDeltaTime + 0.005f, QueryTriggerInteraction.Ignore))
                    if (!hit.collider.transform.IsChildOf(transform)) return true;
                return false;
            }
            body.useGravity = false;
            while (body && past < CoralHalfLength && !Touches(coralEject.forward))
            {
                Tip();
                if (tipEnd >= 0) break;
                past += speed * Time.fixedDeltaTime;
                var dir = coralEject.forward;
                var at = coralEject.position + dir * past;
                body.position = at;
                body.rotation = arm.transform.rotation * relRot;
                body.velocity = armBody.GetPointVelocity(at) + dir * speed;
                body.angularVelocity = armBody.angularVelocity;
                yield return new WaitForFixedUpdate();
            }
            if (body) body.useGravity = true;
            var pushEnd = Time.time + coralContactPushTime;
            while (body && past < CoralHalfLength && Time.time < pushEnd)
            {
                Tip();
                var dir = coralEject.forward;
                past = Vector3.Dot(body.position - coralEject.position, dir);
                var along = Vector3.Dot(body.velocity - armBody.GetPointVelocity(body.position), dir);
                if (tipEnd < 0 && along < speed) body.velocity += dir * Mathf.Min(speed - along, speed * Time.fixedDeltaTime / 0.05f);
                yield return new WaitForFixedUpdate();
            }
            if (l1)
            {
                tipEnd = Mathf.Max(tipEnd, Time.time + 0.2f); // up a moment longer, under its tail
                while (Time.time < tipEnd) yield return null;
                _l1Tipping = false;
            }
            // clear of the rollers: it clears the tube before the arm swings back to stow (the walls would bat it away)
            if (body) StartCoroutine(PassChuteBriefly(body.gameObject));
            _coralLeaving = false;
        }

        // Floor coral steps through the robot state by state (each one once the coral has reached the one before); the
        // last step, into the tube, waits for the arm to be at the receive pose. Station coral goes straight to the tube.
        private void StepCoral(bool hasCoral, bool groundMode)
        {
            if (!hasCoral)
            {
                _coralController.SetTargetState(groundMode && floorCoralStates.Length > 0 ? floorCoralStates[0] : coralState);
                return;
            }

            if (!_coralController.atTarget) return;
            var at = System.Array.FindIndex(floorCoralStates, st => st.stateNum == _coralController.currentStateNum);
            if (at < 0) return; // in the tube
            if (at < floorCoralStates.Length - 1)
                _coralController.SetTargetState(floorCoralStates[at + 1]);
            else if (AtPose(groundIntake, 4f))
                _coralController.SetTargetState(coralState);
        }

        private static bool BaseGameManagerRunning()
        {
            return MoSimCore.BaseClasses.GameManagement.BaseGameManager.Instance.RobotState != RobotState.Disabled;
        }
    }
}
