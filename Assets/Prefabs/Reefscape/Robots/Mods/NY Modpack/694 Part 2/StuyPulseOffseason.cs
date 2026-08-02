using System;
using System.Collections;
using Games.Reefscape.Enums;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using Games.Reefscape.Scoring.Scorers;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.Drivetrain;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYModpack._694
{
    public class StuyPulseOffseason : ReefscapeRobotBase
    {
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint arm;
        [SerializeField] private GenericJoint groundIntake;
        [SerializeField] private GenericJoint climber;
        [SerializeField] private GenericRoller[] intakeRollers;
        [SerializeField] private OffseasonAutoAlign autoAlign;
        [SerializeField] private StuyV3Climber climberObject;
        [SerializeField] private ClimbScorer scorer;
        
        
        [Header("PIDS")]
        [SerializeField] private PidConstants armPid;
        [SerializeField] private PidConstants intakePid;
        [SerializeField] private PidConstants climberPid;

        [Header("coral Setpoints")] 
        [SerializeField] private StuyPulsev3Setpoint stow;
        [SerializeField] private StuyPulsev3Setpoint coralStow;
        [SerializeField] private StuyPulsev3Setpoint groundCoral;
        [SerializeField] private StuyPulsev3Setpoint L1;
        [SerializeField] private StuyPulsev3Setpoint L1High;
        [SerializeField] private StuyPulsev3Setpoint L2;
        [SerializeField] private StuyPulsev3Setpoint L2Place;
        [SerializeField] private StuyPulsev3Setpoint L3;
        [SerializeField] private StuyPulsev3Setpoint L3Place;
        [SerializeField] private StuyPulsev3Setpoint L4;
        [SerializeField] private StuyPulsev3Setpoint L4Place;
        
        [Header("algae Setpoints")]
        [SerializeField] private StuyPulsev3Setpoint lowAlgae;
        [SerializeField] private StuyPulsev3Setpoint highAlgae;
        [SerializeField] private StuyPulsev3Setpoint bargeFront;
        [SerializeField] private StuyPulsev3Setpoint bargeBack;
        [SerializeField] private StuyPulsev3Setpoint groundAlgae;
        [SerializeField] private StuyPulsev3Setpoint algaeStow;
        [SerializeField] private StuyPulsev3Setpoint processor;
        [SerializeField] private StuyPulsev3Setpoint Stack;
        
        [Header("climb Setpoints")]
        [SerializeField] private StuyPulsev3Setpoint climbStow;
        [SerializeField] public StuyPulsev3Setpoint climbPrep;
        [SerializeField] private StuyPulsev3Setpoint climbed;
        
        [Header("Intake Componenets")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;

        [Header("Coral Vision")]
        [SerializeField] private BoxCollider coralVisionZone;
        [SerializeField] private float autoSteerKp = 0.025f;
        [SerializeField] private float maxAutoSteerPower = 0.12f;
        private Collider[] _visionColliders = new Collider[6];
        private LayerMask _coralLayerMask;

        [Header("Algae Vision")]
        [SerializeField] private BoxCollider algaeVisionZone;
        [SerializeField] private float algaeAutoSteerKp = 0.025f;
        [SerializeField] private float algaeMaxAutoSteerPower = 0.12f;
        private LayerMask _algaeLayerMask;
        
        [Header("Game Piece States")]
        [SerializeField] private string currentState;
        [SerializeField] private GamePieceState coralIntakeState;
        [SerializeField] private GamePieceState coralSecondSetpointState;
        [SerializeField] private GamePieceState coralThirdSetpointState;
        [SerializeField] private GamePieceState coralFourthSetpointState;
        [SerializeField] private GamePieceState coralChassisStowState;
        [SerializeField] private GamePieceState coralArmStowState;
        [SerializeField] private GamePieceState algaeStowState;
        [SerializeField] private GamePieceState algaeIntakeState;
        
        [Header("Auto Align Offsets")]
        [SerializeField] private Vector3 initialAutoAlignOffset;
        [SerializeField] private Vector3 algaeAutoAlignOffset;
        [SerializeField] private Vector3 algaeAutoAlignOffsetAlt;  // Add this line
        [SerializeField] private Vector3 l4AutoAlignOffset;
        [SerializeField] private Vector3 l3AutoAlignOffset;
        [SerializeField] private Vector3 l2AutoAlignOffset;
        [SerializeField] private Vector3 bargeAutoAlignOffset;
        
        [Header("Intake Wheels")] [SerializeField]
        private GenericAnimationJoint[] intakeWheels;
        [SerializeField] private float intakeWheelSpeed = 300f;
        [SerializeField]private GenericAnimationJoint[] eEWheels;
        [SerializeField] private float eEWheelSpeed = 300f;


        
        [Header("Algae Stall Audio")]
        [SerializeField] private AudioSource algaeStallSource;
        [SerializeField] private AudioClip algaeStallAudio;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource rollerSource;
        [SerializeField] private AudioClip intakeClip;
        
        [Header("Arm Audio Settings")] [SerializeField]
        private AudioSource armSource;

        [SerializeField] private AudioClip armAudio;
        [SerializeField] private float armAudioMinSpeed = 2f;
        [SerializeField] private float armAudioMaxSpeed = 100f;
        [SerializeField] private Vector2 pitchRange = new(0.7f, 1.5f);
        [SerializeField] private Vector2 volumeRange = new(0.1f, 1f);
        [Range(0, 1)] [SerializeField] private float speedSmoothing = 0.1f;
        private float _previousArmAngle;
        private float _smoothedArmSpeed;

        public RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        public RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;
        
        [Header("Target Positions")]
        private float _elevatorTargetHeight;
        private float _armTargetAngle;
        private float _intakeTargetAngle;
        public float _climberTargetAngle;
        [SerializeField] private float noWrapAngle;

        
        private bool _intakeSequenceRunning;
        private bool _disruptable;
        private bool wasCoral;
        private bool _isPlacingCoral;

        private float _delay;
        private ReefscapeSetpoints? _bufferedSetpoint;
        private bool bufferAlgeaState;
        private bool _facingBarge;

        private bool armNearTarget;

        [Header("Center of Mass")]
        [SerializeField] private bool addCenterOfMassX;
        [SerializeField] private bool addCenterOfMassZ;
        [SerializeField] private float climbedCenterOfMassX;
        [SerializeField] private float climbedCenterOfMassZ;
        private Rigidbody _mainRb;
        private Vector3 _originalCenterOfMass;
        private bool _isCgShifted;

        private DriveController driveController;

        // Start is called before the first frame update

        void Start()
        {
            base.Start();
            
            superCycler = true;
            
            arm.SetPid(armPid);
            groundIntake.SetPid(intakePid);
            climber.SetPid(climberPid);

            _elevatorTargetHeight = 0;
            _armTargetAngle = stow.armAngle;
            _intakeTargetAngle = stow.intakeAngle;
            _climberTargetAngle = stow.climberAngle;
                        
            RobotGamePieceController.SetPreload(coralArmStowState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _algaeController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());
            _coralController.gamePieceStates = new[]
            {
                coralIntakeState, coralSecondSetpointState, coralThirdSetpointState, coralFourthSetpointState,
                coralChassisStowState, coralArmStowState
            };
            _coralController.intakes.Add(coralIntake);

            _algaeController.gamePieceStates = new[] { algaeStowState };
            _algaeController.intakes.Add(algaeIntake);

            _disruptable = true;
            _intakeSequenceRunning = false;
            wasCoral = false;
            _isPlacingCoral = false;
            _bufferedSetpoint = null;
            bufferAlgeaState = false;
            _delay = 200; //its backwards I dont know why
            armNearTarget = false;

            rollerSource.playOnAwake = false;
            rollerSource.clip = intakeClip;
            rollerSource.loop = true;
            rollerSource.Stop();

            algaeStallSource.playOnAwake = false;
            algaeStallSource.clip = algaeStallAudio;
            algaeStallSource.loop = true;
            algaeStallSource.Stop();
            
            armSource.clip = armAudio;
            armSource.loop = true;
            armSource.playOnAwake = false;
            armSource.Stop();
                
            driveController = gameObject.GetComponent<DriveController>();

            _mainRb = gameObject.GetComponent<Rigidbody>();
            _isCgShifted = false;
            if (_mainRb != null)
            {
                _originalCenterOfMass = _mainRb.centerOfMass;
            }
            else
            {
                Debug.LogWarning("ts isnt working btw???");
            }

            _coralLayerMask = LayerMask.GetMask("Coral");
            _algaeLayerMask = LayerMask.GetMask("Algae");
        }

        // Update is called once per frame
        private void LateUpdate()
        {
            arm.UpdatePid(armPid);
            groundIntake.UpdatePid(intakePid);
            climber.UpdatePid(climberPid);
        }

        private void FixedUpdate()
        {
            if (_coralController.HasPiece())
            {
                foreach (var roller in intakeRollers)
                {
                    roller.flipVelocity();
                }
            }
            
            _algaeController.SetTargetState(algaeStowState);
            
            if (_algaeController.HasPiece() || CurrentSetpoint == ReefscapeSetpoints.Barge)
            {
                if (CurrentRobotMode == ReefscapeRobotMode.Coral)
                {
                    wasCoral = true;
                }

                SetRobotMode(ReefscapeRobotMode.Algae);
            }
            else if (_coralController.HasPiece() && CurrentSetpoint == ReefscapeSetpoints.Place)
            {
                SetRobotMode(ReefscapeRobotMode.Coral);
            }

            if (_disruptable && bufferAlgeaState)
            {
                SetRobotMode(ReefscapeRobotMode.Algae);
                bufferAlgeaState = false;
            }
            if (_intakeSequenceRunning || _coralController.IntakeHasPieces(coralIntake))
            {
                if (CurrentSetpoint != ReefscapeSetpoints.Stow && CurrentSetpoint != ReefscapeSetpoints.Intake)
                {
                    _bufferedSetpoint = CurrentSetpoint;
                }
            }

            if (((_coralController.currentStateNum != coralArmStowState.stateNum && !_disruptable) &&
                 !_coralController.atTarget) || _intakeSequenceRunning)
            {
                if (!_disruptable && CurrentRobotMode != ReefscapeRobotMode.Coral && _intakeSequenceRunning &&
                    !_algaeController.HasPiece())
                {
                    bufferAlgeaState = true;
                    SetRobotMode(ReefscapeRobotMode.Coral);
                }
                else if (_disruptable && CurrentRobotMode != ReefscapeRobotMode.Coral)
                {
                }
                else
                {
                    SetState(ReefscapeSetpoints.Stow);
                }
            }

            if ((!_intakeSequenceRunning && CurrentSetpoint != ReefscapeSetpoints.Intake) && _bufferedSetpoint != null)
            {
                SetState(_bufferedSetpoint.Value);
                _bufferedSetpoint = null;
            }

            bool coralAtEE = _coralController.currentStateNum == coralArmStowState.stateNum && _coralController.atTarget;

            if (coralAtEE && CurrentRobotMode != ReefscapeRobotMode.Coral)
            {
                SetRobotMode(ReefscapeRobotMode.Coral);
            }
            
            if ((AutoAlignLeftAction.triggered || AutoAlignRightAction.triggered) &&
                Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), coralStow.armAngle, 1))
            {
                autoAlign.offset = initialAutoAlignOffset;
            }
            
            UpdateIntakeAudio();

            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    if (!_intakeSequenceRunning || _coralController.HasPiece())
                    {
                        SetSetpoint(_algaeController.HasPiece()
                            ? algaeStow
                            : coralStow);
                    }

                    _climberTargetAngle = stow.climberAngle;
                    break;
                case ReefscapeSetpoints.Intake:
                    if (CurrentRobotMode == ReefscapeRobotMode.Algae && !_algaeController.HasPiece())
                    {
                        SetSetpoint(groundAlgae);
                        _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && !coralAtEE);
                        _algaeController.SetTargetState(algaeStowState);
                        foreach (var wheel in eEWheels) 
                            wheel.VelocityRoller(eEWheelSpeed).useAxis(JointAxis.Y);
                    }

                    break;
                case ReefscapeSetpoints.Place:
                    if (LastSetpoint == ReefscapeSetpoints.Stow && coralAtEE)
                    {
                        SetState(ReefscapeSetpoints.Stow);
                        break;
                    }
                
                    if (_algaeController.HasPiece())
                    {
                        _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3f, 0));
                        if (wasCoral)
                        {
                            SetRobotMode(ReefscapeRobotMode.Coral);
                            wasCoral = false;
                        }

                    }
                    else if (CurrentRobotMode != ReefscapeRobotMode.Algae &&
                             _coralController.currentStateNum == coralArmStowState.stateNum && !_isPlacingCoral)
                    {
                        StartCoroutine(PlaceCoral());
                    }
                    else if (_isPlacingCoral)
                    {
                        // Maintain place position after coral is released
                        switch (LastSetpoint)
                        {
                            case ReefscapeSetpoints.L4:
                                autoAlign.offset = l4AutoAlignOffset;
                                SetSetpoint(L4Place);
                                break;
                            case ReefscapeSetpoints.L3:
                                autoAlign.offset = l3AutoAlignOffset;
                                SetSetpoint(L3Place);
                                break;
                            case ReefscapeSetpoints.L2:
                                autoAlign.offset = l2AutoAlignOffset;
                                SetSetpoint(L2Place);
                                break;
                        }
                    }
                    if (OuttakeAction.IsPressed())
                    {
                        switch (LastSetpoint)
                        {
                            case  ReefscapeSetpoints.Barge:
                                foreach (var wheel in eEWheels) 
                                    wheel.VelocityRoller(-eEWheelSpeed).useAxis(JointAxis.Y);
                                break;
                            case  ReefscapeSetpoints.Processor:
                                foreach (var wheel in eEWheels) 
                                    wheel.VelocityRoller(-eEWheelSpeed).useAxis(JointAxis.Y);
                                break;
                            case  ReefscapeSetpoints.L4:
                                foreach (var wheel in eEWheels) 
                                    wheel.VelocityRoller(-eEWheelSpeed).useAxis(JointAxis.Y);
                                break;
                            case  ReefscapeSetpoints.L3:
                                foreach (var wheel in eEWheels) 
                                    wheel.VelocityRoller(-eEWheelSpeed).useAxis(JointAxis.Y);
                                break;
                            case  ReefscapeSetpoints.L2:
                                foreach (var wheel in eEWheels) 
                                    wheel.VelocityRoller(-eEWheelSpeed).useAxis(JointAxis.Y);
                                break;
                            case  ReefscapeSetpoints.L1:
                                foreach (var wheel in eEWheels) 
                                    wheel.VelocityRoller(-eEWheelSpeed).useAxis(JointAxis.Y);
                                break;
                        }

                    }
                    break;
                
                case ReefscapeSetpoints.L1:
                    SetSetpoint(L1);
                    break;
                case ReefscapeSetpoints.L2:
                    autoAlign.offset =
                        Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), L2.armAngle, 20)
                            ? l2AutoAlignOffset
                            : initialAutoAlignOffset;
                    SetSetpoint(L2);
                    break;
                case ReefscapeSetpoints.LowAlgae:
                {
                    bool flip = ComputeAlignFlip();
                    if (AutoAlignLeftAction.IsPressed())
                        autoAlign.offset = !flip ? algaeAutoAlignOffset : algaeAutoAlignOffsetAlt;
                    else if (AutoAlignRightAction.IsPressed())
                        autoAlign.offset = !flip ? algaeAutoAlignOffsetAlt : algaeAutoAlignOffset;

                    SetSetpoint(lowAlgae);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && !coralAtEE);
                    _algaeController.SetTargetState(algaeStowState);
                    if (IntakeAction.IsPressed())
                    {
                        foreach (var wheel in eEWheels)
                            wheel.VelocityRoller(eEWheelSpeed).useAxis(JointAxis.Y);
                    }
                    break;
                }
                case ReefscapeSetpoints.L3:
                    autoAlign.offset = FacingReef
                        ? Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), L3.armAngle, 20)
                            ? l3AutoAlignOffset
                            : initialAutoAlignOffset
                        : l3AutoAlignOffset;
                    SetSetpoint(L3);
                    break;
                case ReefscapeSetpoints.HighAlgae:
                {
                    bool flip = ComputeAlignFlip();
                    if (AutoAlignLeftAction.IsPressed())
                        autoAlign.offset = !flip ? algaeAutoAlignOffset : algaeAutoAlignOffsetAlt;
                    else if (AutoAlignRightAction.IsPressed())
                        autoAlign.offset = !flip ? algaeAutoAlignOffsetAlt : algaeAutoAlignOffset;

                    if (IntakeAction.IsPressed())
                    {
                        foreach (var wheel in eEWheels)
                            wheel.VelocityRoller(eEWheelSpeed).useAxis(JointAxis.Y);
                    }
                    SetSetpoint(highAlgae);
                    _algaeController.RequestIntake(algaeIntake, IntakeAction.IsPressed() && !coralAtEE);
                    _algaeController.SetTargetState(algaeStowState);
                    break;
                }
                case ReefscapeSetpoints.L4:
                    autoAlign.offset = FacingReef
                        ? Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), L4.armAngle, 20)
                            ? l4AutoAlignOffset
                            : initialAutoAlignOffset
                        : l4AutoAlignOffset;
                    SetSetpoint(L4);
                    break;
                case ReefscapeSetpoints.Stack:
                    SetSetpoint(Stack);
                    
                    _algaeController.RequestIntake(
                        algaeIntake,
                        IntakeAction.IsPressed() && !coralAtEE
                    );

                    _algaeController.SetTargetState(algaeStowState);
                    
                    if (IntakeAction.IsPressed())
                    {
                        foreach (var wheel in eEWheels) 
                            wheel.VelocityRoller(eEWheelSpeed).useAxis(JointAxis.Y);
                    }
                    break;
                case ReefscapeSetpoints.Barge:
                    autoAlign.bargeOffset = bargeAutoAlignOffset;
                    CheckFacingBarge();
    
                    StuyPulsev3Setpoint targetBargeSetpoint = _facingBarge ? bargeFront : bargeBack;
    
                    // Get current elevator height
                    float currentElevHeight = elevator.GetElevatorHeight();
                    float targetBargeElevHeight = targetBargeSetpoint.elevatorHeight;
    
                    // Check if elevator is near target height (within 1 inch tolerance)
                    bool elevatorNearTarget = Mathf.Abs(currentElevHeight - targetBargeElevHeight) <= 1f;
    
                    if (elevatorNearTarget)
                    {
                        // Elevator is at height, set full setpoint including arm
                        SetSetpoint(targetBargeSetpoint);
                    }
                    else
                    {
                        // Elevator not at height yet, only move elevator and keep arm at current position
                        _elevatorTargetHeight = targetBargeSetpoint.elevatorHeight;
                        _intakeTargetAngle = targetBargeSetpoint.intakeAngle;
                        _climberTargetAngle = targetBargeSetpoint.climberAngle;
                        // _armTargetAngle stays at current value
                    }

                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    SetSetpoint(L1High);
                    // Otherwise do nothing, stay at current setpoint
                    break;
                case ReefscapeSetpoints.Climb:
                    SetSetpoint(climbPrep);
                    climberObject.Climb();
                    break;
                case ReefscapeSetpoints.Climbed:
                    SetSetpoint(climbed);
                    climberObject.NotClimbing();
                    break;
                case ReefscapeSetpoints.Processor:
                    SetSetpoint(processor);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            OPIntakeSequence();
            
            SetSetpoints();
            
            // If not climbing or climbed, set climber to not climbing
            if (CurrentSetpoint != ReefscapeSetpoints.Climb)
            {
                climberObject.NotClimbing();
            }
            
            if (scorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climb)// && climberObject.WingsOpen())
            {
                climberObject.PlayClick();
                SetState(ReefscapeSetpoints.Climbed);
            }
            else if (!scorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climbed)
                SetState(ReefscapeSetpoints.Climb);

            if (_mainRb != null)
            {
                if (CurrentSetpoint == ReefscapeSetpoints.Climbed)
                {
                    if (!_isCgShifted)
                    {
                        _mainRb.centerOfMass = new Vector3(climbedCenterOfMassX, _originalCenterOfMass.y, climbedCenterOfMassZ);
                        _isCgShifted = true;
                    }
                }
                else if (_isCgShifted)
                {
                    _mainRb.centerOfMass = _originalCenterOfMass;
                    _isCgShifted = false;
                }
            }

            RunCoralVision();
            RunAlgaeVision();
        }

        private void RunCoralVision()
        {
            // Abort if no vision box is assigned, the button isn't held, or the robot already has a piece
            if (coralVisionZone == null) return;
            if (!IntakeAction.IsPressed() || _coralController.HasPiece() || _coralController.IntakeHasPieces(coralIntake)) return;
            if (CurrentRobotMode != ReefscapeRobotMode.Coral) return;

            // Clear the array
            for (int i = 0; i < _visionColliders.Length; i++) _visionColliders[i] = null;

            // Scan the invisible box for corals
            int hits = Physics.OverlapBoxNonAlloc(coralVisionZone.bounds.center, coralVisionZone.bounds.extents, _visionColliders, coralVisionZone.transform.rotation, _coralLayerMask);

            if (hits == 0 || _visionColliders[0] == null) return;

            // Find the closest coral in the box
            GameObject closestCoral = _visionColliders[0].gameObject;
            float closestDist = Vector3.Distance(closestCoral.transform.position, transform.position);

            for (int i = 1; i < hits; i++)
            {
                if (_visionColliders[i] == null) continue;
                float dist = Vector3.Distance(_visionColliders[i].transform.position, transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestCoral = _visionColliders[i].gameObject;
                }
            }

            // Calculate the angle between the robot's front and the closest coral
            Vector3 directionToCoral = closestCoral.transform.position - transform.position;
            directionToCoral.y = 0; // Ignore height differences
            Vector3 forward = transform.forward;
            forward.y = 0;

            float angleError = Vector3.SignedAngle(forward, directionToCoral, Vector3.up);

            // Calculate tuning and clamp the power
            float steerPower = -angleError * autoSteerKp;
            steerPower = Mathf.Clamp(steerPower, -maxAutoSteerPower, maxAutoSteerPower);

            // Inject the steering command directly into the Swerve Drive Controller
            if (driveController != null && Mathf.Abs(steerPower) > 0.01f)
            {
                driveController.SoftSteer(steerPower);
            }
        }

        private void RunAlgaeVision()
        {
        
            if (algaeVisionZone == null) return;
            if (!IntakeAction.IsPressed() || _algaeController.HasPiece() || _algaeController.IntakeHasPieces(algaeIntake)) return;
            if (CurrentRobotMode != ReefscapeRobotMode.Algae) return;

            for (int i = 0; i < _visionColliders.Length; i++) _visionColliders[i] = null;

            int hits = Physics.OverlapBoxNonAlloc(algaeVisionZone.bounds.center, algaeVisionZone.bounds.extents, _visionColliders, algaeVisionZone.transform.rotation, _algaeLayerMask);

            if (hits == 0 || _visionColliders[0] == null) return;

            GameObject closestAlgae = _visionColliders[0].gameObject;
            float closestDist = Vector3.Distance(closestAlgae.transform.position, transform.position);

            for (int i = 1; i < hits; i++)
            {
                if (_visionColliders[i] == null) continue;
                float dist = Vector3.Distance(_visionColliders[i].transform.position, transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestAlgae = _visionColliders[i].gameObject;
                }
            }

            Vector3 directionToAlgae = closestAlgae.transform.position - transform.position;
            directionToAlgae.y = 0; // Ignore height differences
            Vector3 forward = -transform.forward; // Algae intake faces the back of the robot
            forward.y = 0;

            float angleError = Vector3.SignedAngle(forward, directionToAlgae, Vector3.up);

            float steerPower = -angleError * algaeAutoSteerKp;
            steerPower = Mathf.Clamp(steerPower, -algaeMaxAutoSteerPower, algaeMaxAutoSteerPower);

            if (driveController != null && Mathf.Abs(steerPower) > 0.01f)
            {
                driveController.SoftSteer(steerPower);
            }
        }
        private IEnumerator PlaceCoral()
        {
            _isPlacingCoral = true;

            switch (LastSetpoint)
            {
                case ReefscapeSetpoints.L4:
                    SetSetpoint(L4Place);
                    yield return new WaitForSeconds(0.1f);
                    yield return new WaitUntil(()=> _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 0)));

                    break;
                case ReefscapeSetpoints.L3:
                    SetSetpoint(L3Place);
                    yield return new WaitUntil(()=> _coralController.ReleaseGamePieceWithForce(new Vector3(0,0.5f, 2.5f)));

                    break;
                case ReefscapeSetpoints.L2:
                    SetSetpoint(L2Place);
                    yield return new WaitUntil(()=> _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0.5f, 2.5f)));

                    break;
            }
            
            // yield return new WaitForSeconds(0.1f);
            if (LastSetpoint == ReefscapeSetpoints.L1 || LastSetpoint == ReefscapeSetpoints.RobotSpecial)
            {
                // L1 and RobotSpecial use sideways release
                yield return new WaitUntil(()=> _coralController.ReleaseGamePieceWithForce(new Vector3(0, -1.5f, 0)));

            }
        }
        
        private bool ComputeAlignFlip()
        {
            var flip = false;
            if (GetActiveCamera().transform.eulerAngles.y < 180) flip = !flip;
            if (Mathf.Abs(transform.position.x) > 4.489323f && PlayerPrefs.GetInt("PerspectiveAutoAlign", 1) == 1) flip = !flip;
            if (transform.position.x > 0) flip = !flip;
            return flip;
        }

        private void CheckFacingBarge()
        {
            var toZAxisXY = new Vector3(-transform.position.x, -transform.position.y, 0f).normalized;
            var forwardXY = new Vector3(transform.forward.x, transform.forward.y, 0f).normalized;
            var dot = Vector3.Dot(forwardXY, toZAxisXY);
            _facingBarge = dot > 0.0f;
        }
        
        private void OPIntakeSequence()
        {
            if (!IntakeAction.IsPressed())
            {
                _intakeSequenceRunning = false;
                if (!_coralController.HasPiece())
                {
                    _disruptable = true;
                    // Reset placing flag when intake is released and no coral
                    if (_isPlacingCoral)
                    {
                        _isPlacingCoral = false;
                    }
                }
            }

            if (CurrentRobotMode == ReefscapeRobotMode.Coral ||
                (_algaeController.HasPiece() && CurrentRobotMode == ReefscapeRobotMode.Algae))
            {
                if (CurrentSetpoint != ReefscapeSetpoints.HighAlgae && CurrentSetpoint != ReefscapeSetpoints.LowAlgae &&
                    CurrentSetpoint != ReefscapeSetpoints.Barge && CurrentSetpoint != ReefscapeSetpoints.Place /*&&!
                        algaeController.HasPiece()*/)
                {
                    bool hasAlgea = _algaeController.HasPiece();
                    _coralController.RequestIntake(coralIntake, IntakeAction.IsPressed());
                    
                    if (IntakeAction.IsPressed() ||
                        (_coralController.HasPiece() && _coralController.currentStateNum != coralArmStowState.stateNum))
                    {
                        _disruptable = false;
                        _intakeSequenceRunning = true;

                        _armTargetAngle = hasAlgea ? _armTargetAngle : groundCoral.armAngle;
                        _elevatorTargetHeight = hasAlgea ? _elevatorTargetHeight : coralStow.elevatorHeight;
                        _intakeTargetAngle = groundCoral.intakeAngle;

                        _coralController.SetTargetState(_coralController.currentStateNum switch
                        {
                            0 => nameof(coralIntakeState),
                            1 => nameof(coralSecondSetpointState),
                            2 => nameof(coralThirdSetpointState),
                            3 => nameof(coralFourthSetpointState),
                            4 => nameof(coralChassisStowState),
                            _ => _coralController.movingTo
                        });

                        if (BaseGameManager.Instance.RobotState == RobotState.Enabled &&
                            Mathf.Approximately(_intakeTargetAngle, groundCoral.intakeAngle) && _coralController.currentStateNum < 5)
                        {
                            foreach (var wheel in intakeWheels)
                            {
                                wheel.VelocityRoller(intakeWheelSpeed).useAxis(JointAxis.X);
                            }
                        }
                        
                        bool atChasisStow = _coralController.currentStateNum == coralChassisStowState.stateNum &&
                                            _coralController.atTarget;
                        if (atChasisStow)
                        {
                            _armTargetAngle = hasAlgea ? _armTargetAngle : coralStow.armAngle;
                            _intakeTargetAngle = coralStow.intakeAngle;  // ← Add this line

                            if (Utils.WithinAngularRange(arm.GetSingleAxisAngle(JointAxis.X), -coralStow.armAngle, 5))
                            {
                                _elevatorTargetHeight = hasAlgea ? _elevatorTargetHeight : groundCoral.elevatorHeight;
                                foreach (var wheel in eEWheels) 
                                    wheel.VelocityRoller(eEWheelSpeed).useAxis(JointAxis.Y);

                            }
                            else
                            {
                                _elevatorTargetHeight = hasAlgea ? _elevatorTargetHeight : coralStow.elevatorHeight;
                            }

                            _disruptable = true;
                            

                            // Check if elevator is at handoff height
                            float elev = elevator.GetElevatorHeight();
                            bool elevatorAtCoralGrab = Mathf.Abs(elev - groundCoral.elevatorHeight) <= 1f;

                            // Only complete handoff when arm is at stow AND elevator is at handoff height
                            if (elevatorAtCoralGrab && _coralController.atTarget &&
                                Mathf.Approximately(_elevatorTargetHeight, groundCoral.elevatorHeight) && 
                                Utils.WithinAngularRange(arm.GetSingleAxisAngle(JointAxis.X), -coralStow.armAngle, 3f))
                            {
                                _coralController.SetTargetState(coralArmStowState);
                            }
                        }
                        bool atArmStow = _coralController.atTarget && _coralController.currentStateNum == coralArmStowState.stateNum;
                        if (atArmStow)
                        {
                            SetState(ReefscapeSetpoints.Stow);
                            _intakeSequenceRunning = false;
                        }
                    }
                    else if ((_coralController.atTarget && _coralController.currentStateNum == coralArmStowState.stateNum) &&
                             _intakeSequenceRunning)
                    {
                        SetState(ReefscapeSetpoints.Stow);
                        _intakeSequenceRunning = false;
                    }
                }
            }
        }

        private void UpdateIntakeAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (rollerSource.isPlaying || algaeStallSource.isPlaying)
                {
                    rollerSource.Stop();
                    algaeStallSource.Stop();
                }

                return;
            }

            var intakeAudioActive = (IntakeAction.IsPressed() && _coralController.currentStateNum != coralArmStowState.stateNum)
                                    || OuttakeAction.IsPressed()
                                    || (_coralController.HasPiece()
                                        && _coralController.currentStateNum != coralChassisStowState.stateNum
                                        && _coralController.currentStateNum != coralArmStowState.stateNum);
            if (!rollerSource.isPlaying && intakeAudioActive)
            {
                rollerSource.Play();
            }
            else if (rollerSource.isPlaying && !intakeAudioActive)
            {
                rollerSource.Stop();
            }
            if (_algaeController.HasPiece() && !algaeStallSource.isPlaying)
            {
                algaeStallSource.Play();
            }
            else if (!_algaeController.HasPiece() && algaeStallSource.isPlaying)
            {
                algaeStallSource.Stop();
            }
        }
        private void UpdateArmPivotAudio()
        {
            var currentPivotAngle = Utils.FlipAngle(arm.GetSingleAxisAngle(JointAxis.X));
            var rawSpeed = Mathf.Abs(currentPivotAngle - _previousArmAngle) / Time.fixedDeltaTime;
            _previousArmAngle = currentPivotAngle;

            _smoothedArmSpeed = Mathf.Lerp(_smoothedArmSpeed, rawSpeed, 1f - speedSmoothing);

            var t = Mathf.InverseLerp(armAudioMinSpeed, armAudioMaxSpeed, _smoothedArmSpeed);

            armSource.pitch = Mathf.Lerp(pitchRange.x, pitchRange.y, t);
            armSource.volume = Mathf.Lerp(volumeRange.x, volumeRange.y, t);

            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (armSource.isPlaying)
                {
                    armSource.Stop();
                }

                return;
            }
            
            if (Mathf.Abs(_smoothedArmSpeed) > armAudioMinSpeed)
            {
                if (!armSource.isPlaying)
                {
                    armSource.Play();
                }
            }
            else if (armSource.isPlaying)
            {
                armSource.Stop();
            }
        }
            
        private void SetSetpoint(StuyPulsev3Setpoint setpoint)
        {
            if ((CurrentSetpoint == ReefscapeSetpoints.Climb || CurrentSetpoint == ReefscapeSetpoints.Climbed) &&
                _algaeController.HasPiece())
            {
                _armTargetAngle = groundAlgae.armAngle;
                _elevatorTargetHeight = groundAlgae.elevatorHeight;
                _intakeTargetAngle = setpoint.intakeAngle;
                _climberTargetAngle = setpoint.climberAngle;
            }
            else
            {
                _armTargetAngle = setpoint.armAngle;
                _elevatorTargetHeight = setpoint.elevatorHeight;
                _intakeTargetAngle = setpoint.intakeAngle;
                _climberTargetAngle = setpoint.climberAngle;
            }
            
        }
        
        private void SetSetpoints()
        {

            arm.SetTargetAngle(_armTargetAngle).withAxis(JointAxis.X).noWrap(noWrapAngle);
            
            elevator.SetTarget(_elevatorTargetHeight);

            groundIntake.SetTargetAngle(_intakeTargetAngle).withAxis(JointAxis.X);//.noWrap(90);

            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.X).useAutomaticStartingOffset()
                .noWrap(180f);
        }
    }
}