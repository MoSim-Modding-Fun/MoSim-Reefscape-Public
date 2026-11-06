using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using System.Linq;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.Spartans._971
{
    public class Spartans: ReefscapeRobotBase
    {
        #region Serialized Fields and Variables
        
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
            [SerializeField] private GenericJoint arm, wrist, intake, intakeFlap, climber;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants armPID;
            [SerializeField] private PidConstants wristPID, climberPID, intakePID, intakeFlapPID;

        [Header("Setpoints")]
        [SerializeField] private SpartansSetpoint stow;
            [SerializeField] private SpartansSetpoint groundIntake1, groundIntake2, groundIntake3;
            [SerializeField] private SpartansSetpoint hpIntakeFront, hpIntakeBack;
            [SerializeField] private SpartansSetpoint l1Front, l1Back, l2Front, l2Back, l3Front, l3Back, l4Front, l4Back;
            [SerializeField] private SpartansSetpoint groundAlgae, lollipop, lowAlgaeFront, lowAlgaeBack, highAlgaeFront, highAlgaeBack, proc, bargeFront, bargeBack;
            [SerializeField] private SpartansSetpoint climbPrep, climbClimb;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
            [SerializeField] private ReefscapeGamePieceIntake groundCoralIntake, hpCoralIntake;

        [Header("Rollers")]
        [Tooltip("Claw compliant wheels; a positive speed pulls a piece in.")]
        [SerializeField] private GenericAnimationJoint[] clawRollers;
        [Tooltip("Ground intake rollers; a positive speed pulls a coral in.")]
        [SerializeField] private GenericAnimationJoint[] intakeAnimationRollers;
        [Tooltip("Climber cage-grab wheels; a positive speed pulls a cage post into the climber.")]
        [SerializeField] private GenericAnimationJoint[] climberRollers;
        [Tooltip("Physics bodies of the climber cage-grab wheels (same order and direction as climberRollers).")]
        [SerializeField] private GenericRoller[] climberWheels;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState algaeStowState;
            [SerializeField] private GamePieceState groundIntakeState, endEffectorState;
        
        [Header("Robot Audio")]
        [Tooltip("Looping sounds while the claw rollers, the ground intake rollers or the climber's grab wheels spin.")]
        [SerializeField] private AudioClip clawRollerClip, groundRollerClip, climberWheelClip;
        [Tooltip("One-shots: the clickers latching on a cage post, and a piece leaving the claw.")]
        [SerializeField] private AudioClip latchClip, releaseClip;
        private AudioSource _rollerAudio, _oneShotAudio;
        private float _clawRollerSpeed, _groundRollerSpeed;
        private bool _climberWheelsOn;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController, _algaeController;

        private float _elevatorTargetHeight, _armTargetAngle, _wristTargetAngle, _intakeTargetAngle, _climberTargetAngle;

        // Trapezoidal motion profiles from 971's competition-season code (y2025 common.json): each mechanism's command
        // ramps toward its setpoint with a cruise speed and an acceleration limit, and the joint PIDs follow it.
        // Elevator 15 m/s, 20 m/s^2 (full rise to L4 ~0.53 s, as on their video); pivot 5 rad/s, 15 rad/s^2; wrist
        // 10 rad/s, 40 rad/s^2.
        private static readonly (float vel, float accel) ElevatorProfile = (15f, 20f);      // m/s, m/s^2
        private static readonly (float vel, float accel) ArmProfile = (286f, 859f);         // deg/s, deg/s^2
        private static readonly (float vel, float accel) WristProfile = (573f, 2292f);
        private static readonly (float vel, float accel) IntakeProfile = (252f, 7200f);
        // Coming in it brakes early: the pivot's torque limit can't stop it from full speed, and it overshot into the elevator.
        private static readonly (float vel, float accel) IntakeStowProfile = (252f, 900f);
        // The swing down to the ground intake pose is quicker than the real code's general pivot limits (requested).
        private static readonly (float vel, float accel) GroundArmProfile = (520f, 2600f);
        private float _elevatorCmd, _armCmd, _wristCmd, _intakeCmd;
        private float _elevatorVel, _armVel, _wristVel, _intakeVel;
        private float _intakeFlapTargetAngle = 0;

        private ReefscapeAutoAlign _align;
        private AlignNode[] _reefFaces;
        private static readonly System.Reflection.MethodInfo AlignPositionMethod = typeof(RobotFramework.Controllers.Drivetrain.AutoAlign)
            .GetMethod("AlignPosition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                new[] { typeof(Vector3), typeof(Quaternion) }, null);
        private float _alignTrim;
        // The claw's sideways offset from the robot's centre line, smoothed over ~0.4 s: the arm swings sideways, so
        // its live position sways with the robot's own motion, and feeding that straight back into the target made the
        // robot and arm rock each other.
        private float _clawOffset;
        private bool _clawOffsetValid;
        private const float ClawOffsetSmoothing = 0.4f;

        // Real roller voltages scaled to a speed that still reads on screen.
        private const float WheelSpeedPerVolt = 1440f / 12f;

        // The physical ground intake rollers (and their motor speeds) only run while intaking.
        private (GenericRoller roller, float speed)[] _intakeRollers;
        private float _placeTime = -10f;

        // Cage grabber on the climber: spring-loaded clickers that let a cage post in and close behind it, and the
        // cage-grab wheels that pull a post in (the real robot runs them at -10 V until the cage stalls them).
        private GenericAnimationJoint[] _clickers, _latchClickers;
        private FixedJoint _climbWeld;
        private ConfigurableJoint _cageLatch;
        private Collider _latchedPost;
        private const float ClickerMaxOpen = 90f;
        private float _climbedSince = float.PositiveInfinity;
        private BoxCollider _cageTrigger;
        private CapsuleCollider _climberBackStop;
        private bool _cageGrabbed;

        
        private bool _isRetractingFromGround;
        private SpartansSetpoint _pendingSetpoint;

        // Like the real robot's SuperstructurePathCommand: a path moves on to its next waypoint once every joint is
        // within these (elevator m, pivot deg, wrist deg, intake deg) of the current one, so the moves blend.
        private static readonly (float elevator, float arm, float wrist, float intake) GroundPathTolerance = (0.4f, 120f, 20f, 123f);
        private static readonly (float elevator, float arm, float wrist, float intake) L1PathTolerance = (0.25f, 40f, 100f, 100f);
        private static readonly (float elevator, float arm, float wrist, float intake) GroundCaptureTolerance = (0.05f, 10f, 10f, 10f);
        // Real robot: past this pivot angle toward the intake's side, the ground intake is swung down out of the arm's way.
        private const float PivotAvoidanceAngle = 100f;
        private const int SolverIterations = 200, SolverVelocityIterations = 75;
        private const float MaxPlaceWait = 1.2f;         // s
        private const float PlaceAlignTolerance = 0.006f; // m
        private const float PlaceSettledSpeed = 0.08f;    // m/s
        private float _placeAskedAt = -1;
        // Algae align offsets in inches, larger is closer to the reef: where it lines up first, and how far in it may creep.
        private const float AlgaeAlignStageZ = 3f, AlgaeAlignLimitZ = 9f;
        private const float AlgaeCreepSpeed = 0.2f;      // m/s
        private const float AlgaeStageTolerance = 0.015f; // m
        private const float ClawWheelRadius = 0.054f;    // m
        private const float AlgaeContactMargin = 0.01f;  // m
        // Deg out from the real robot's stow: on this model a hub on the intake otherwise ends up inside the elevator's base tube.
        private const float IntakeStowClearance = 9f;
        private float _algaeAlignZ = AlgaeAlignStageZ, _faceAlignError = float.PositiveInfinity;
        private bool _algaeCreeping;
        private readonly Collider[] _nearAlgae = new Collider[8];
        private int _algaeMask;
        private const float CoralLeaveTime = 1f;         // s
        private const float CoralClearDistance = 0.5f;   // m, claw to coral centre
        private Collider[] _armColliders, _leavingColliders;
        private Component _leavingCoral;
        private float _leavingUntil;
        private Rigidbody _robotBody;
        // L1 is a two-waypoint path: raise and pivot together with the wrist stowed, then the wrist turns.
        private SpartansSetpoint _l1FrontEntry, _l1BackEntry, _l1RetractVia;

        private bool _coralViaEntry;

        // Speed the claw rollers spit the algae out at the barge (m/s).
        private const float BargeSpitSpeed = 3f;

        // The ground intake roller's sprung mount: soft only while ground intaking so the roller can ride up over a
        // coral; otherwise its joint gets the stiff drive the joint stabilizer gives every other joint (its prefab damper
        // of 2 made the stabilizer leave it weak), so the stowed flap stays put.
        private ConfigurableJoint _compliance, _intakePivot;
        private const float ArmHardStop = 165f;
        private const float IntakePivotTorque = 80f; // N m: the deploy profile's acceleration plus gravity, with margin
        private static readonly JointDrive ComplianceStiff = new() { positionSpring = 0f, positionDamper = 2f * Mathf.Sqrt(999999f), maximumForce = float.MaxValue };
        private static readonly JointDrive ComplianceSoft = new() { positionSpring = 5f, positionDamper = 1f, maximumForce = 90f };

        // A coral being spat out of the claw by its rollers.
        private Rigidbody _ejecting;
        private float _ejectUntil;
        private SpartansSetpoint _retractFromWaypoint;
        
        private Vector3 _blueReef = new Vector3(-4.298872f, 0, 0);
        private Vector3 _redReef = new Vector3(4.298872f, 0, 0);
        
        private Vector3 _blueProcHp, _blueNonProcHp;
        private Vector3 _redProcHp, _redNonProcHp;

        private Vector3 _closestHp;
        
        #endregion
        
        protected override void Start()
        {
            base.Start();
            
            arm.SetPid(armPID);
            wrist.SetPid(wristPID);
            intake.SetPid(intakePID);
            intakeFlap.SetPid(intakeFlapPID);
            climber.SetPid(climberPID);

            _l1FrontEntry = PathEntry(l1Front);
            _l1BackEntry = PathEntry(l1Back);

            _intakePivot = intake.GetComponent<ConfigurableJoint>();
            // The pivot's hard stops (the real one turns +-160 deg from straight up): however the inputs are mashed, the
            // arm can't swing down through the robot. Every pose is within +-153 deg.
            var armJoint = arm.GetComponent<ConfigurableJoint>();
            armJoint.angularXMotion = ConfigurableJointMotion.Limited;
            armJoint.lowAngularXLimit = new SoftJointLimit { limit = -ArmHardStop };
            armJoint.highAngularXLimit = new SoftJointLimit { limit = ArmHardStop };
            _compliance = intake.transform.Find("ComplianceThing").GetComponent<ConfigurableJoint>();
            // The arm and wrist have no side play, but at the framework's default iterations the joint chain gives when a
            // hit stops the 98 kg robot within a step: the arm twisted 3 deg off its hinge. More iterations hold it to 0.3.
            _robotBody = GetComponent<Rigidbody>();
            _algaeMask = LayerMask.GetMask("Algae");
            _armColliders = arm.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
            var stabilizer = GetComponent<JointStackStabilizer>();
            stabilizer.solverIterations = SolverIterations;
            stabilizer.solverVelocityIterations = SolverVelocityIterations;

            _elevatorTargetHeight = stow.elevatorHeight;
            _armTargetAngle = stow.armAngle;
            _wristTargetAngle = stow.wristAngle;
            _climberTargetAngle = stow.climberAngle;
            _intakeTargetAngle = stow.intakeAngle;
            _intakeFlapTargetAngle = 0;
            _elevatorCmd = _elevatorTargetHeight;
            _armCmd = _armTargetAngle;
            _wristCmd = _wristTargetAngle;
            _intakeCmd = _intakeTargetAngle;
            
            _align = GetComponent<ReefscapeAutoAlign>();
            _reefFaces = GameObject.FindGameObjectsWithTag("ReefFace").Select(g => g.GetComponent<AlignNode>()).Where(n => n).ToArray();
            var rollers = intake.GetComponentsInChildren<GenericRoller>(true);
            _intakeRollers = new (GenericRoller, float)[rollers.Length];
            for (var i = 0; i < rollers.Length; i++)
                _intakeRollers[i] = (rollers[i], rollers[i].GetComponent<HingeJoint>().motor.targetVelocity);
            _clickers = climber.GetComponentsInChildren<GenericAnimationJoint>()
                .Where(j => System.Array.IndexOf(climberRollers, j) < 0).ToArray();
            _latchClickers = _clickers.Where(c => c.name.StartsWith("Clicker")).ToArray();
            _cageTrigger = climber.transform.Find("Cage Trigger")?.GetComponent<BoxCollider>();
            // The cross standoff at the back of the climber head (the mouth faces away from it).
            _climberBackStop = climber.GetComponentsInChildren<CapsuleCollider>(true).FirstOrDefault(c => c.name == "Great Standoff");

            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
            _algaeController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));

            _coralController.gamePieceStates = new[]
            {
                groundIntakeState,
                endEffectorState
            };
            _coralController.intakes.Add(groundCoralIntake);
            _coralController.intakes.Add(hpCoralIntake);
            
            RobotGamePieceController.SetPreload(endEffectorState);

            _algaeController.gamePieceStates = new[]
            {
                algaeStowState
            };
            _algaeController.intakes.Add(algaeIntake);
            
            var robotSounds = Resources.Load<UnityEngine.Audio.AudioMixerGroup>("MainAudioMixer").audioMixer.FindMatchingGroups("RobotSounds")[0];
            AudioSource Source(Component host, AudioClip clip, bool loop)
            {
                var source = host.gameObject.AddComponent<AudioSource>();
                source.outputAudioMixerGroup = robotSounds;
                source.playOnAwake = false;
                source.loop = loop;
                source.clip = clip;
                return source;
            }
            _rollerAudio = Source(this, null, true);
            _oneShotAudio = Source(this, null, false);
            
            _blueProcHp = GameObject.Find("Coral Station").transform.position;
            _blueNonProcHp = GameObject.Find("Coral Station (1)").transform.position;
            _redProcHp = GameObject.Find("Coral Station (3)").transform.position;
            _redNonProcHp = GameObject.Find("Coral Station (2)").transform.position;
        }

        protected override void Update()
        {
            base.Update();
            // Sprung while driving onto the cage; latched shut once climbing so a cage post behind them stays trapped.
            var latched = CurrentSetpoint == ReefscapeSetpoints.Climbed;
            foreach (var clicker in _clickers)
            {
                if (latched) clicker.transform.localRotation = Quaternion.identity;
                else
                {
                    clicker.SpringLoaded().AllowedDirection(1).RotationSpeed(220);
                    // The flaps swing back against their holders; they never turn past fully open.
                    var open = Mathf.Repeat(clicker.transform.localEulerAngles.y, 360);
                    if (System.Array.IndexOf(_latchClickers, clicker) >= 0 && open > ClickerMaxOpen && open < 270) clicker.transform.localRotation = Quaternion.Euler(0, ClickerMaxOpen, 0);
                }
            }
        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPID);
            wrist.UpdatePid(wristPID);
            intake.UpdatePid(intakePID);
            intakeFlap.UpdatePid(intakeFlapPID);
            climber.UpdatePid(climberPID);
        }

        // A coral the ground intake has started pulling in is handed all the way to the claw even if the intake button is
        // let go (for up to 1.5 s), instead of being dropped mid-transfer and left on the stowed intake.
        private float _lastIntakePress = float.NegativeInfinity;
        private bool GroundHandoff => groundCoralIntake && groundCoralIntake.hasGamePiece && !groundCoralIntake.securedGamePiece &&
                                      !_coralController.HasPiece() && CurrentSetpointIs(groundIntake3) && Time.time - _lastIntakePress < 1.5f;

        private void FixedUpdate()
        {
            if (CurrentSetpoint != ReefscapeSetpoints.Place) _placeAskedAt = -1;
            UpdateLeavingCoral();
            if (IntakeAction.IsPressed()) _lastIntakePress = Time.time;
            SpinWheels();
            EjectCoral();
            bool hasCoral = _coralController.HasPiece();
            bool hasAlgae = _algaeController.HasPiece();
            
            if (_l1RetractVia)
            {
                if (SuperstructureNear(_l1RetractVia, L1PathTolerance))
                {
                    SetSetpoint(_pendingSetpoint);
                    _l1RetractVia = null;
                }
                UpdateSetpoints();
                UpdateAudio();
                return;
            }

            if (_isRetractingFromGround)
            {
                GoFromGround();
                if (SuperstructureNear(stow, GroundPathTolerance))
                {
                    _isRetractingFromGround = false;
                    SetSetpoint(_pendingSetpoint);
                }
                UpdateSetpoints();
                UpdateAudio();
                return;
            }
            
            _coralController.RequestIntake(hpCoralIntake, IntakeAction.IsPressed() && (SuperstructureAtSetpoint(hpIntakeFront) || SuperstructureAtSetpoint(hpIntakeBack)));
            // Like the real ground path: the rollers run as soon as the path is on its last waypoint and nearly there.
            _coralController.RequestIntake(groundCoralIntake, (IntakeAction.IsPressed() || GroundHandoff) && CurrentSetpointIs(groundIntake3) &&
                                                              SuperstructureNear(groundIntake3, GroundCaptureTolerance));
            _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && IsAlgaeSetpoint() && (algaeIntake.hasGamePiece || AlgaeAtWheels()));
            _algaeController.SetTargetState(algaeStowState);
            UpdateClimb();
            // A coral picked up off the ground goes to the claw's entry first, then slides straight into the claw.
            if (!_coralController.HasPiece()) _coralViaEntry = CurrentSetpointIs(groundIntake3);
            else if (_coralViaEntry && _coralController.atTarget &&
                     Vector3.Distance(_coralController.controller.transform.position, groundIntakeState.stateTarget.position) < 0.01f)
                _coralViaEntry = false;
            var coralState = _coralViaEntry ? groundIntakeState : endEffectorState;
            SyncCoralTurn(coralState);
            _coralController.SetTargetState(coralState);
            
            switch (GroundHandoff ? ReefscapeSetpoints.Intake : CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: RequestSetpoint(stow); break;
                case ReefscapeSetpoints.Intake:
                    if (hasCoral || hasAlgae) break;
                    
                    if (CurrentRobotMode == ReefscapeRobotMode.Algae)
                    {
                        if (!IsAlgaeSetpoint()) RequestSetpoint(groundAlgae);
                        else _algaeController.RequestIntake(algaeIntake, algaeIntake.hasGamePiece || AlgaeAtWheels());
                    }
                    else
                    {
                        if (CurrentCoralStationMode.DropType == DropType.Ground)
                        {
                            GoToGround();
                        }
                        else
                        {
                            RequestSetpoint(IsFacingHp() ? hpIntakeBack : hpIntakeFront);
                        }
                    }

                    break;
                
                case ReefscapeSetpoints.Processor: RequestSetpoint(proc); break;
                case ReefscapeSetpoints.Stack: RequestSetpoint(lollipop); break;
                case ReefscapeSetpoints.Barge: RequestSetpoint(IsFacing(new Vector3(0, 0, transform.position.z)) ? bargeFront : bargeBack); break;
                
                case ReefscapeSetpoints.LowAlgae: RequestSetpoint(IsFacing(GetClosestReef()) ? lowAlgaeFront : lowAlgaeBack); break;
                case ReefscapeSetpoints.HighAlgae: RequestSetpoint(IsFacing(GetClosestReef()) ? highAlgaeFront : highAlgaeBack); break;
                
                case ReefscapeSetpoints.L1:
                    if (_coralController.atTarget)
                    {
                        // Intake-side L1 is off: L1 always scores over the claw side.
                        // GoToL1(IsFacing(GetClosestReef()));
                        GoToL1(true);
                    }
                    break;
                case ReefscapeSetpoints.L2:
                    if (_coralController.atTarget)
                    {
                        RequestSetpoint(IsFacing(GetClosestReef()) ? l2Front : l2Back);
                    } 
                    break;
                case ReefscapeSetpoints.L3:
                    if (_coralController.atTarget)
                    {
                        RequestSetpoint(IsFacing(GetClosestReef()) ? l3Front : l3Back);
                    } 
                    break;
                case ReefscapeSetpoints.L4:
                    if (_coralController.atTarget)
                    {
                        RequestSetpoint(IsFacing(GetClosestReef()) ? l4Front : l4Back);
                    } 
                    break;
                
                case ReefscapeSetpoints.Climb: RequestSetpoint(climbPrep); break;
                case ReefscapeSetpoints.Climbed: RequestSetpoint(climbClimb); break;
                
                case ReefscapeSetpoints.Place: PlacePiece(); break;
                
                case ReefscapeSetpoints.RobotSpecial: 
                    CurrentCoralStationMode.DropType = (CurrentCoralStationMode.DropType == DropType.Ground ? DropType.Station : DropType.Ground);
                    CurrentCoralStationMode.DropOrientation = (CurrentCoralStationMode.DropOrientation == DropOrientation.Horizontal ? DropOrientation.Vertical : DropOrientation.Horizontal); 
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }
            
            UpdateAlignTrim();
            UpdateSetpoints();
            UpdateAudio();
        }

        #region Actuators & Setpoints
        
        private void SetSetpoint(SpartansSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
            _armTargetAngle = setpoint.armAngle;
            _wristTargetAngle = setpoint.wristAngle;
            _intakeTargetAngle = setpoint.intakeAngle;
            _climberTargetAngle = setpoint.climberAngle;
        }

        private float MetersToInches(float met)
        {
            return (float)(met * 39.3700787402);
        }

        private void UpdateSetpoints()
        {
            var dt = Time.fixedDeltaTime;
            _elevatorCmd = Profile(_elevatorCmd, ref _elevatorVel, _elevatorTargetHeight, ElevatorProfile, dt);
            var toGround = CurrentSetpointIs(groundIntake2) || CurrentSetpointIs(groundIntake3);
            _armCmd = Profile(_armCmd, ref _armVel, _armTargetAngle, toGround ? GroundArmProfile : ArmProfile, dt);
            _wristCmd = Profile(_wristCmd, ref _wristVel, _wristTargetAngle, WristProfile, dt);
            // The real code's check is one-sided (pivot > 100 deg, the side the intake is on); this robot's arm angles
            // are the real ones negated. It looks at where the arm is going as well as where it is, so the intake is out
            // of the way before the claw sweeps down over it (raise and pivot move together).
            var armOverIntake = -_armCmd > PivotAvoidanceAngle || -_armTargetAngle > PivotAvoidanceAngle;
            var intakeTarget = armOverIntake ? groundIntake3.intakeAngle : Mathf.Max(_intakeTargetAngle, IntakeStowClearance);
            _intakeCmd = Profile(_intakeCmd, ref _intakeVel, intakeTarget, intakeTarget < _intakeCmd ? IntakeStowProfile : IntakeProfile, dt);

            elevator.SetTarget(MetersToInches(_elevatorCmd));
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.Y)
                .useCustomStartingOffset(70);
            // The arm never passes straight down through the robot (the real pivot's range is +-160 deg).
            arm.SetTargetAngle(_armCmd).withAxis(JointAxis.X)
                .noWrap(180)
                .useCustomStartingOffset(0);
            wrist.SetTargetAngle(_wristCmd - 90).withAxis(JointAxis.X)
                .noWrap(180)
                .useCustomStartingOffset(0);
            intake.SetTargetAngle(_intakeCmd).withAxis(JointAxis.Y)
                .useCustomStartingOffset(0);
            // The pivot motor has a torque limit: landing on a coral presses it instead of crushing through it (an
            // unlimited drive pinched the coral against the carpet and shot it out). Brake mode owns the drive while
            // disabled.
            if (BaseGameManager.Instance.RobotState != RobotState.Disabled)
            {
                var pivotDrive = _intakePivot.angularYZDrive;
                pivotDrive.maximumForce = IntakePivotTorque;
                _intakePivot.angularYZDrive = pivotDrive;
            }
            intakeFlap.SetTargetAngle(_intakeFlapTargetAngle).withAxis(JointAxis.X)
                .useCustomStartingOffset(0);
        }

        // One step of a trapezoidal profile from cmd toward target: accelerate, cruise, then brake to stop on it.
        private static float Profile(float cmd, ref float vel, float target, (float vel, float accel) limits, float dt)
        {
            var error = target - cmd;
            if (Mathf.Abs(error) < 1e-4f && Mathf.Abs(vel) <= limits.accel * dt)
            {
                vel = 0;
                return target;
            }
            var desired = Mathf.Sign(error) * Mathf.Min(limits.vel, Mathf.Sqrt(2 * limits.accel * Mathf.Abs(error)));
            vel = Mathf.MoveTowards(vel, desired, limits.accel * dt);
            var next = cmd + vel * dt;
            if (Mathf.Sign(target - next) != Mathf.Sign(error))
            {
                vel = 0;
                return target;
            }
            return next;
        }

        private void GoToGround()
        {
            if (CurrentSetpointIs(groundIntake3)) return;
            
            if (CurrentSetpointIs(groundIntake2) && SuperstructureNear(groundIntake2, GroundPathTolerance))
            {
                SetSetpoint(groundIntake3);
            }
            else if (CurrentSetpointIs(groundIntake1) && SuperstructureNear(groundIntake1, GroundPathTolerance))
            {
                SetSetpoint(groundIntake2);
            }
            else if (!CurrentSetpointIs(groundIntake2) && !CurrentSetpointIs(groundIntake3))
            {
                SetSetpoint(groundIntake1);
            }
        }
        
        private void GoFromGround()
        {
            if (CurrentSetpointIs(stow)) return;

            if (CurrentSetpointIs(groundIntake3) && SuperstructureNear(groundIntake3, GroundPathTolerance))
            {
                SetSetpoint(groundIntake2);
            }
            else if (CurrentSetpointIs(groundIntake2) && SuperstructureNear(groundIntake2, GroundPathTolerance))
            {
                SetSetpoint(groundIntake1);
            }
            else if (CurrentSetpointIs(groundIntake1) && SuperstructureNear(groundIntake1, GroundPathTolerance))
            {
                SetSetpoint(stow);
            }
        }

        private bool SuperstructureAtSetpoint(SpartansSetpoint setpoint)
        {
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), MetersToInches(setpoint.elevatorHeight), 2f);
            
            bool armAtSetpoint = Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X) - 283, setpoint.armAngle, 2f);
            bool wristAtSetpoint = Utils.InAngularRange(wrist.GetSingleAxisAngle(JointAxis.X) - 355, setpoint.wristAngle - 90, 2f);
            // (The ground intake stays stowed out of the way; its exact angle doesn't matter for the claw.)
            return elevatorAtSetpoint && wristAtSetpoint && armAtSetpoint;
        }

        private bool SuperstructureNear(SpartansSetpoint setpoint, (float elevator, float arm, float wrist, float intake) tolerance)
        {
            return Utils.InRange(elevator.GetElevatorHeight(), MetersToInches(setpoint.elevatorHeight), MetersToInches(tolerance.elevator)) &&
                   Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X) - 283, setpoint.armAngle, tolerance.arm) &&
                   Utils.InAngularRange(wrist.GetSingleAxisAngle(JointAxis.X) - 355, setpoint.wristAngle - 90, tolerance.wrist) &&
                   Utils.InAngularRange(intake.GetSingleAxisAngle(JointAxis.Y) + 71.4f, setpoint.intakeAngle, tolerance.intake);
        }

        // L1's path: first waypoint at the L1 height with the arm straight up, then the L1 pose itself.
        private void GoToL1(bool front)
        {
            var target = front ? l1Front : l1Back;
            var entry = front ? _l1FrontEntry : _l1BackEntry;
            if (CurrentSetpointIs(target)) return;
            if (CurrentSetpointIs(entry))
            {
                if (SuperstructureNear(entry, L1PathTolerance)) SetSetpoint(target);
            }
            else RequestSetpoint(entry);
        }

        // L1's waypoint: the elevator raises and the arm pivots together with the wrist still stowed; the wrist turns
        // last (971: "raise + pivot, then wrist"). Retracting goes back through it, wrist first.
        private SpartansSetpoint PathEntry(SpartansSetpoint setpoint)
        {
            var entry = Instantiate(setpoint);
            entry.wristAngle = stow.wristAngle;
            return entry;
        }

        private bool CurrentSetpointIs(SpartansSetpoint setpoint)
        {
            return
                _elevatorTargetHeight == setpoint.elevatorHeight &&
                _armTargetAngle == setpoint.armAngle &&
                _wristTargetAngle == setpoint.wristAngle &&
                _intakeTargetAngle == setpoint.intakeAngle;
        }

        private bool IsAlgaeSetpoint()
        {
            return CurrentSetpointIs(groundAlgae) ||
                   CurrentSetpointIs(lollipop) ||
                   CurrentSetpointIs(lowAlgaeFront) ||
                   CurrentSetpointIs(highAlgaeFront) ||
                   CurrentSetpointIs(lowAlgaeBack) ||
                   CurrentSetpointIs(highAlgaeBack);
        }
        
        private bool IsInGroundSequence()
        {
            return CurrentSetpointIs(groundIntake1) || 
                   CurrentSetpointIs(groundIntake2) || 
                   CurrentSetpointIs(groundIntake3);
        }

        private void RequestSetpoint(SpartansSetpoint setpoint)
        {
            if (IsInGroundSequence() && CurrentSetpoint != ReefscapeSetpoints.Intake)
            {
                // Back out the way the path came in, from the waypoint it is on.
                _isRetractingFromGround = true;
                _pendingSetpoint = setpoint;
            }
            else if ((CurrentSetpointIs(l1Front) || CurrentSetpointIs(l1Back)) && setpoint != l1Front && setpoint != l1Back)
            {
                // Leaving L1: swing the arm back up through the path's first waypoint, then go.
                _l1RetractVia = CurrentSetpointIs(l1Front) ? _l1FrontEntry : _l1BackEntry;
                _pendingSetpoint = setpoint;
                SetSetpoint(_l1RetractVia);
            }
            else
            {
                SetSetpoint(setpoint);
            }
        }
        
        #endregion
        

        #region Logic Helpers
        
        private float DistanceTo(Vector3 itemPos)
        {
            return Mathf.Sqrt(Mathf.Pow(transform.position.x - itemPos.x, 2) + Mathf.Pow(transform.position.z - itemPos.z, 2));
        }
    
        private Vector3 GetClosestReef()
        {
            //print("Facing " + (DistanceTo(_blueReef) < DistanceTo(_redReef) ? _blueReef : _redReef) + " with distance of " + DistanceTo(DistanceTo(_blueReef) < DistanceTo(_redReef) ? _blueReef : _redReef));
            return DistanceTo(_blueReef) < DistanceTo(_redReef) ? _blueReef : _redReef;
        }

        private bool IsFacing(Vector3 itemPos)
        {
            var toReefVector = (itemPos - transform.position).normalized;
            var robotForwardVector = transform.forward.normalized;
            var angle = Vector3.Dot(robotForwardVector, toReefVector);
            return angle > 0.0f;
        }

        private Vector3 GetClosestHp(Vector3 procHp, Vector3 nonProcHp)
        {
            return DistanceTo(procHp) < DistanceTo(nonProcHp) ? procHp : nonProcHp;
        }
        
        private bool IsFacingHp()
        {
            if (GetClosestReef() == _blueReef)
            {
                _closestHp = GetClosestHp(_blueProcHp, _blueNonProcHp);
            }
            else
            {
                _closestHp = GetClosestHp(_redProcHp, _redNonProcHp);
            }

            return IsFacing(_closestHp);
        }
        
        // One roller sound at a time: the climber's grab wheels while climbing, else the claw rollers, else the ground
        // intake's. The joints and the elevator play their own movement sounds.
        private void UpdateAudio()
        {
            if (!_rollerAudio) return;
            var on = BaseGameManager.Instance.RobotState != RobotState.Disabled;
            var clip = !on ? null : _climberWheelsOn ? climberWheelClip : _clawRollerSpeed != 0 ? clawRollerClip : _groundRollerSpeed != 0 ? groundRollerClip : null;
            _rollerAudio.pitch = clip == clawRollerClip && _clawRollerSpeed < 0 ? 1.15f : 1f;
            if (!clip)
            {
                if (_rollerAudio.isPlaying) _rollerAudio.Stop();
                return;
            }
            if (_rollerAudio.clip != clip) _rollerAudio.clip = clip;
            if (!_rollerAudio.isPlaying) _rollerAudio.Play();
        }

        private void PlayOnce(AudioClip clip)
        {
            if (_oneShotAudio && clip) _oneShotAudio.PlayOneShot(clip);
        }

        private bool SuperstructureAtTargets() =>
            Utils.InRange(elevator.GetElevatorHeight(), MetersToInches(_elevatorTargetHeight), 1f) &&
            Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X) - 283, _armTargetAngle, 2f) &&
            Utils.InAngularRange(wrist.GetSingleAxisAngle(JointAxis.X) - 355, _wristTargetAngle - 90, 3f);

        // Waits out a place at L2-L4 until the coral will actually land. Fires at once when already settled; never waits
        // longer than MaxPlaceWait, so a blocked robot can still let go.
        private bool PlaceMustWait()
        {
            if (!_coralController.HasPiece() || LastSetpoint is not (ReefscapeSetpoints.L2 or ReefscapeSetpoints.L3 or ReefscapeSetpoints.L4)) return false;
            if (_placeAskedAt < 0) _placeAskedAt = Time.time;
            if (Time.time - _placeAskedAt > MaxPlaceWait) return false;
            if (!SuperstructureAtTargets()) return true;
            // On the align a robot that looks there can still be 1-3 cm off the pole, which is a miss.
            var aligning = AutoAlignLeftAction.IsPressed() != AutoAlignRightAction.IsPressed();
            return aligning && (_align.getDistance() > PlaceAlignTolerance || _robotBody.velocity.magnitude > PlaceSettledSpeed);
        }

        // A coral sliding out between the claw's rollers can't be batted sideways by the plates either side of it, but
        // here a claw that moves in the first 0.15 s knocks an L4 coral down to the trough. So the coral ignores the arm
        // and claw until it has cleared them.
        private void LetCoralLeave(Component coral)
        {
            RestoreCoralContact();
            _leavingCoral = coral;
            _leavingColliders = coral.GetComponentsInChildren<Collider>(true);
            _leavingUntil = Time.time + CoralLeaveTime;
            foreach (var a in _leavingColliders)
            foreach (var b in _armColliders)
                Physics.IgnoreCollision(a, b);
        }

        private void RestoreCoralContact()
        {
            if (_leavingColliders == null) return;
            foreach (var a in _leavingColliders)
            foreach (var b in _armColliders)
                if (a && b) Physics.IgnoreCollision(a, b, false);
            _leavingColliders = null;
            _leavingCoral = null;
        }

        private void UpdateLeavingCoral()
        {
            if (_leavingColliders == null) return;
            if (!_leavingCoral || Time.time > _leavingUntil ||
                Vector3.Distance(_leavingCoral.transform.position, endEffectorState.stateTarget.position) > CoralClearDistance)
                RestoreCoralContact();
        }

        private void PlacePiece()
        {
            if (PlaceMustWait()) return;
            var coral = _coralController.HasPiece() ? _coralController.controller : null;
            ReleasePiece();
            // After the release: it re-enables the coral's colliders, and that clears any IgnoreCollision on them.
            if (coral && !_coralController.HasPiece()) LetCoralLeave(coral);
        }

        private void ReleasePiece()
        {
            if (_coralController.HasPiece() || _algaeController.HasPiece())
            {
                _placeTime = Time.time;
                PlayOnce(releaseClip);
            }
            if (_algaeController.HasPiece())
            {
                var piece = _algaeController.controller;
                if (!piece) return;
                Vector3 velocity;
                if (LastSetpoint == ReefscapeSetpoints.Barge)
                {
                    // Like the real robot's code: no extra motion at the barge, the claw rollers spit the ball out
                    // along the claw (the barge pose holds the claw up and tilted toward the net).
                    velocity = endEffectorState.stateTarget.forward * BargeSpitSpeed;
                }
                else
                {
                    // Straight out of the side of the robot the claw is on.
                    var side = Mathf.Sign(transform.InverseTransformPoint(piece.transform.position).z);
                    velocity = transform.forward * (side * 2f);
                }
                ReleaseAt(_algaeController, velocity);
                return;
            }

            if (!_coralController.controller) return;
            if (LastSetpoint == ReefscapeSetpoints.L1)
            {
                // L1: the rollers spit the coral out along the claw, into the trough. Facing away the claw reaches the
                // trough over the robot, so the coral needs more speed to clear it.
                var back = Vector3.Dot(_coralController.controller.transform.position - transform.position, transform.forward) < 0;
                ReleaseAt(_coralController, _coralController.controller.transform.forward * (back ? 3.5f : 2f));
                return;
            }
            if (LastSetpoint == ReefscapeSetpoints.L4)
            {
                // L4: like the real robot, the claw rollers keep spitting the coral out along the claw's line (aimed
                // down at the pipe) until it has left the claw, so it drops all the way onto the pipe.
                var coral = _coralController.controller.GetComponent<Rigidbody>();
                ReleaseAt(_coralController, _coralController.controller.transform.forward * 1f);
                _ejecting = coral;
                _ejectUntil = Time.time + 0.6f;
                return;
            }
            // The held coral's +Z points out of the claw toward the branch (front and back poses alike).
            // L2 / L3: out of the claw along the coral's own axis; the robot lines the claw up with the branch beforehand.
            ReleaseAt(_coralController, _coralController.controller.transform.forward * 3f);
        }

        private void UpdateClimb()
        {
            var climbing = CurrentSetpoint is ReefscapeSetpoints.Climb or ReefscapeSetpoints.Climbed;
            DriveController.SetDriveMp(climbing ? 0.5f : 1f);

            // A cage post pushed between the grab wheels into the plates' notch has passed the clickers, which close in
            // front of it: it is trapped in the notch (held there by a joint, standing in for the hooks), and the climber
            // pulls on its own.
            if (CurrentSetpoint == ReefscapeSetpoints.Climb && !_cageLatch && SeatedPost(out var post, out var seat))
                LatchCage(post, seat);
            else if (!climbing && _cageLatch) ReleaseCage();
            _cageGrabbed = _cageLatch;
            if (CurrentSetpoint != ReefscapeSetpoints.Climbed) _climbedSince = float.PositiveInfinity;
            else if (float.IsPositiveInfinity(_climbedSince)) _climbedSince = Time.time;

            // Hanging: when the climber has finished pulling, weld it so the robot stays up; let go when leaving.
            var climberBody = climber.GetComponent<Rigidbody>();
            if (CurrentSetpoint == ReefscapeSetpoints.Climbed && !_climbWeld && Time.time - _climbedSince > 1f &&
                climberBody.angularVelocity.magnitude < 0.2f)
            {
                _climbWeld = climber.gameObject.AddComponent<FixedJoint>();
                _climbWeld.connectedBody = climber.GetComponent<ConfigurableJoint>().connectedBody;
            }
            else if (CurrentSetpoint != ReefscapeSetpoints.Climbed && _climbWeld)
            {
                Destroy(_climbWeld);
                _climbWeld = null;
            }

            // Cage-grab wheels run while the climber is deployed and nothing is latched yet: their physics bodies pull a
            // cage post in between the plates.
            var wheelsRunning = CurrentSetpoint == ReefscapeSetpoints.Climb && !_cageGrabbed &&
                                BaseGameManager.Instance.RobotState != RobotState.Disabled;
            var wheelSpeed = wheelsRunning ? 10f * WheelSpeedPerVolt : 0;
            foreach (var wheel in climberRollers) wheel.VelocityRoller(wheelSpeed);
            foreach (var wheel in climberWheels) wheel.SetAngularVelocity(wheelSpeed);
            _climberWheelsOn = wheelsRunning;
        }

        // The grab wheels' axles are normal to the climber plates; the mouth is between the wheels, facing away from
        // the standoff. A post is seated once its axis crosses the plates within 1.2 cm of the mouth's centre line and
        // at least 1 cm deeper than the wheel axles (the notch bottoms out ~4 cm past them).
        private bool SeatedPost(out Collider post, out Vector3 seat)
        {
            post = null;
            seat = default;
            if (!_cageTrigger || !_climberBackStop || climberWheels.Length < 2) return false;
            var w0 = climberWheels[0].GetComponentInChildren<CapsuleCollider>();
            var w1 = climberWheels[1].GetComponentInChildren<CapsuleCollider>();
            var c0 = w0.transform.TransformPoint(w0.center);
            var c1 = w1.transform.TransformPoint(w1.center);
            var normal = w0.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[w0.direction]).normalized;
            var mid = (c0 + c1) / 2;
            var across = Vector3.ProjectOnPlane(c1 - c0, normal).normalized;
            var outward = Vector3.ProjectOnPlane(mid - _climberBackStop.bounds.center, normal);
            outward = Vector3.ProjectOnPlane(outward, across).normalized;
            var box = _cageTrigger.transform;
            foreach (var hit in Physics.OverlapBox(box.position, Vector3.Scale(_cageTrigger.size, box.lossyScale) / 2, box.rotation, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!hit.CompareTag("Cage") || hit is not CapsuleCollider capsule) continue;
                var axis = capsule.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[capsule.direction]).normalized;
                var cos = Vector3.Dot(axis, normal);
                if (Mathf.Abs(cos) < 0.5f) continue;
                var centre = capsule.transform.TransformPoint(capsule.center);
                var crossing = centre + axis * (Vector3.Dot(mid - centre, normal) / cos);
                var d = crossing - mid;
                if (Mathf.Abs(Vector3.Dot(d, across)) < 0.012f && Vector3.Dot(d, outward) < -0.01f)
                {
                    post = capsule;
                    seat = crossing;
                    return true;
                }
            }
            return false;
        }

        // Pins the post where it sits in the notch: it can turn about its own axis but not move or tilt in the plates.
        private void LatchCage(Collider post, Vector3 seat)
        {
            var axle = climberWheels[0].GetComponentInChildren<CapsuleCollider>();
            var normal = axle.transform.TransformDirection(new[] { Vector3.right, Vector3.up, Vector3.forward }[axle.direction]).normalized;
            var t = climber.transform;
            _cageLatch = climber.gameObject.AddComponent<ConfigurableJoint>();
            _cageLatch.connectedBody = post.attachedRigidbody;
            _cageLatch.anchor = t.InverseTransformPoint(seat);
            _cageLatch.axis = t.InverseTransformDirection(normal);
            _cageLatch.secondaryAxis = t.InverseTransformDirection(Vector3.ProjectOnPlane(t.up, normal).normalized);
            _cageLatch.xMotion = _cageLatch.yMotion = _cageLatch.zMotion = ConfigurableJointMotion.Locked;
            _cageLatch.angularXMotion = ConfigurableJointMotion.Free;
            _cageLatch.angularYMotion = _cageLatch.angularZMotion = ConfigurableJointMotion.Locked;
            // The hooks and plates now hold the post through the joint; their boxes no longer fight it.
            _latchedPost = post;
            PlayOnce(latchClip);
            foreach (var c in climber.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(c, post, true);
            SetState(ReefscapeSetpoints.Climbed);
        }

        private void ReleaseCage()
        {
            Destroy(_cageLatch);
            _cageLatch = null;
            if (_latchedPost)
                foreach (var c in climber.GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(c, _latchedPost, false);
            _latchedPost = null;
        }

        // A coral handed from state to state turns at the rate that finishes its turn just as it arrives, so it is
        // seen being straightened on the way in rather than spinning first and sliding after.
        private void SyncCoralTurn(GamePieceState state)
        {
            var piece = _coralController.controller;
            if (!piece || !state.stateTarget || _coralController.atTarget) return;
            var key = state.name.Trim().ToLower();
            if (!_coralController.StateLookup.TryGetValue(key, out var data)) return;
            var target = state.stateTarget;
            var seconds = Vector3.Distance(piece.transform.position, target.position) / (state.stateMoveSpeed * 0.0254f);
            var angle = Quaternion.Angle(piece.transform.rotation, target.rotation);
            data.stateAngularSpeed = Mathf.Clamp(angle / Mathf.Max(seconds, 0.05f), 30f, 900f);
            _coralController.StateLookup[key] = data;
        }

        // The claw rollers (spitting at -10 V) push a released coral along the claw until it is clear of the claw.
        private void EjectCoral()
        {
            if (!_ejecting) return;
            var claw = endEffectorState.stateTarget;
            var outward = claw.forward;
            var clear = Vector3.Dot(_ejecting.position - claw.position, outward) > 0.3f;
            if (Time.time > _ejectUntil || clear)
            {
                _ejecting = null;
                return;
            }
            if (Vector3.Dot(_ejecting.velocity, outward) < 1.5f) _ejecting.AddForce(outward * 20f, ForceMode.Acceleration);
        }

        // Claw wheels spin in while intaking, out while placing; the ground intake spins while it is picking up.
        private void SpinWheels()
        {
            var running = BaseGameManager.Instance.RobotState != RobotState.Disabled;
            var placing = Time.time - _placeTime < 0.6f;
            var empty = !_coralController.HasPiece() && !_algaeController.HasPiece();
            // A coral still travelling to the claw (e.g. handed up from the ground intake) is being pulled in.
            var coralMoving = _coralController.HasPiece() && !_coralController.atTarget;
            // Real roller voltages scaled to a speed that still reads on screen: claw intake 12 V, spit -10 V; ground
            // intake 5 V.
            var clawSpeed = !running ? 0 : placing ? -10f * WheelSpeedPerVolt : (IntakeAction.IsPressed() && empty) || coralMoving ? 12f * WheelSpeedPerVolt : 0;
            var groundMode = (CurrentSetpoint == ReefscapeSetpoints.Intake || GroundHandoff) && CurrentRobotMode == ReefscapeRobotMode.Coral &&
                             CurrentCoralStationMode.DropType == DropType.Ground;
            // The intake rollers only run once the arm, wrist and intake are at the intake pose (0 V on the way down),
            // so a coral is never pushed in before the claw is there to take it.
            var groundIntaking = running && groundMode && empty && CurrentSetpointIs(groundIntake3) &&
                                 SuperstructureNear(groundIntake3, GroundCaptureTolerance);
            _compliance.angularXDrive = groundMode ? ComplianceSoft : ComplianceStiff;
            var intakeSpeed = groundIntaking || (running && groundMode && coralMoving) ? 5f * WheelSpeedPerVolt : 0;

            _clawRollerSpeed = clawSpeed;
            _groundRollerSpeed = intakeSpeed;
            foreach (var roller in clawRollers) roller.VelocityRoller(clawSpeed);
            foreach (var roller in intakeAnimationRollers) roller.VelocityRoller(intakeSpeed);
            foreach (var (roller, speed) in _intakeRollers)
                if (roller) roller.SetAngularVelocity(groundIntaking ? speed : 0);
        }

        // While auto-aligning at a branch level, slide the alignment target sideways until the claw lines up with
        // the nearest branch of that level. The claw sits off the robot's centre line, mirrored when facing away.
        private void UpdateAlignTrim()
        {
            if (!_align) return;

            var aligning = AutoAlignLeftAction.IsPressed() || AutoAlignRightAction.IsPressed();
            // Both held: the real robot's CENTER align, on the middle of the nearest reef face. (The stock align would
            // chase the left and right poles at once through one set of PIDs and drive off.)
            // Algae sits in the middle of a face, so at a reef algae setpoint either button lines up there.
            var algaeAlign = aligning && CurrentSetpoint is ReefscapeSetpoints.LowAlgae or ReefscapeSetpoints.HighAlgae;
            var centre = algaeAlign || (AutoAlignLeftAction.IsPressed() && AutoAlignRightAction.IsPressed());
            _align.enabled = !centre;
            if (!algaeAlign)
            {
                _algaeAlignZ = AlgaeAlignStageZ;
                _algaeCreeping = false;
                _faceAlignError = float.PositiveInfinity;
            }
            if (centre) AlignToFaceCentre(algaeAlign ? AlgaeApproachZ() : _align.offset.z);
            var level = CurrentSetpoint switch
            {
                ReefscapeSetpoints.L1 => "L1",
                ReefscapeSetpoints.L2 => "L2",
                ReefscapeSetpoints.L3 => "L3",
                ReefscapeSetpoints.L4 => "L4",
                _ => null
            };
            var claw = endEffectorState.stateTarget;

            if (!aligning || centre || level == "L1") // L1 scores anywhere along the trough: no sideways trim
            {
                _alignTrim = 0;
                _clawOffsetValid = false;
            }
            else if (level != null && claw && TryGetNearestBranch(level, claw.position, out var branch) && TryGetNearestPole(branch, out var pole))
            {
                // Straight to the final spot: the pole's align node, shifted sideways by where this level's branch sits
                // off the node minus where the claw sits off the robot's centre line. Both are fixed, so the target
                // doesn't move while driving in. The align node faces the reef, so its right is the robot's right when
                // facing the reef and its left when facing away.
                var side = GetFacingReef() ? 1f : -1f;
                var branchOffset = Vector3.Dot(branch - pole.position, pole.right);
                var clawOffset = Vector3.Dot(claw.position - transform.position, transform.right) * side;
                _clawOffset = _clawOffsetValid ? Mathf.Lerp(_clawOffset, clawOffset, Time.fixedDeltaTime / ClawOffsetSmoothing) : clawOffset;
                _clawOffsetValid = true;
                _alignTrim = Mathf.Clamp(branchOffset - _clawOffset, -0.15f, 0.15f);
            }

            _align.offset.x = _alignTrim / 0.0254f;
        }

        // The middle of the nearest reef face, at the align's usual stand-off, facing it or facing away like the poles.
        // Lines up a few inches out, then, with the arm at its pose, creeps in and stops as soon as the wheels touch the ball.
        private float AlgaeApproachZ()
        {
            if (_algaeController.HasPiece() || AlgaeAtWheels()) return _algaeAlignZ;
            _algaeCreeping |= _faceAlignError < AlgaeStageTolerance && SuperstructureAtTargets();
            if (_algaeCreeping)
                _algaeAlignZ = Mathf.MoveTowards(_algaeAlignZ, AlgaeAlignLimitZ, AlgaeCreepSpeed / 0.0254f * Time.fixedDeltaTime);
            return _algaeAlignZ;
        }

        // The ball touching any claw wheel: only then can the wheels take it. (They have no colliders of their own.)
        private bool AlgaeAtWheels()
        {
            var count = Physics.OverlapSphereNonAlloc(algaeIntake.transform.position, 0.6f, _nearAlgae, _algaeMask);
            for (var i = 0; i < count; i++)
            {
                if (!_nearAlgae[i].CompareTag("Algae")) continue;
                var reach = _nearAlgae[i].bounds.extents.x + ClawWheelRadius + AlgaeContactMargin;
                foreach (var wheel in clawRollers)
                    if ((wheel.transform.position - _nearAlgae[i].transform.position).sqrMagnitude < reach * reach) return true;
            }
            return false;
        }

        private void AlignToFaceCentre(float offsetZ)
        {
            var face = _reefFaces.Where(n => n && n.LeftNode && n.RightNode)
                .OrderBy(n => (n.transform.position - transform.position).sqrMagnitude).FirstOrDefault();
            if (!face || Vector3.Distance(face.transform.position, transform.position) > 15f * 0.3048f) return;
            var pole = face.LeftNode.transform;
            var rotation = pole.rotation;
            if ((!GetFacingReef() && _align.enableBackwardsAlign) || !_align.enableForwardAlign) rotation *= Quaternion.Euler(0, 180, 0);
            var target = (face.LeftNode.transform.position + face.RightNode.transform.position) / 2 +
                         pole.rotation * new Vector3(0, 0, offsetZ * 0.0254f);
            _faceAlignError = Vector3.ProjectOnPlane(target - transform.position, Vector3.up).magnitude;
            AlignPositionMethod.Invoke(_align, new object[] { target, rotation * Quaternion.Euler(0, _align.rotation, 0) });
        }

        // Centre of the nearest reef scoring trigger for this level (L2 / L3 / L4) within reach of the claw.
        // The reef align node (a face's left or right pole node) nearest a point.
        private bool TryGetNearestPole(Vector3 from, out Transform pole)
        {
            pole = _reefFaces.Where(n => n && n.LeftNode && n.RightNode)
                .SelectMany(n => new[] { n.LeftNode.transform, n.RightNode.transform })
                .OrderBy(n => (n.position - from).sqrMagnitude).FirstOrDefault();
            return pole;
        }

        private static bool TryGetNearestBranch(string level, Vector3 from, out Vector3 branch)
        {
            branch = default;
            var best = 1.2f;
            foreach (var pole in FindObjectsByType<ReefPoleScorer>(FindObjectsSortMode.None))
            {
                foreach (var trigger in pole.GetComponentsInChildren<BoxCollider>(true))
                {
                    if (trigger.name != level) continue;
                    var distance = Vector3.Distance(trigger.bounds.center, from);
                    if (distance >= best) continue;
                    best = distance;
                    branch = trigger.bounds.center;
                }
            }
            return best < 1.2f;
        }

        // Releases a held piece and gives it exactly this world velocity (no spin), so it leaves the claw at a
        // set speed and direction.
        private static void ReleaseAt(
            RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode node,
            Vector3 velocity)
        {
            var piece = node.controller;
            if (!piece) return;

            var rb = piece.GetComponent<Rigidbody>();
            if (!node.ReleaseGamePieceWithForce(Vector3.zero) || !rb) return;
            rb.velocity = velocity;
            rb.angularVelocity = Vector3.zero;
        }
        
        #endregion
    }
}