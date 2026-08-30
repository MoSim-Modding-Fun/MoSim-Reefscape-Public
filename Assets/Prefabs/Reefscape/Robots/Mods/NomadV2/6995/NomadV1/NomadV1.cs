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
using Robots.Climbing;
using System.Collections;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NomadV2Mod._6995
{
    public class NomadV2 : ReefscapeRobotBase
    {
        [Header("Robot Components")]
        [SerializeField] private GenericJoint armJoint;
        [SerializeField] private GenericJoint wristJoint;
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericRoller leftIntakeRollerJoint;
        [SerializeField] private GenericRoller rightIntakeRollerJoint;
        [SerializeField] private GenericRoller topIntakeRoller;
        [SerializeField] private Transform leftIntakeSensor;
        [SerializeField] private Transform rightIntakeSensor;
        [SerializeField] private Transform algaeSlider;
        [SerializeField] private NomadV2Climber climber;

        // =========================================================
        // INTAKE FLAP - ADDED
        // =========================================================

        [Header("Intake Flap")]
        [SerializeField] private GenericJoint intakeFlap;
        [SerializeField] private PidConstants intakeFlapPid;

        [Tooltip("Normal resting angle of the flap.")]
        [SerializeField] private float flapRestAngle = 0f;

        [Tooltip("Maximum angle the flap moves to when touching a game piece.")]
        [SerializeField] private float flapIntakeAngle = 12f;

        [Tooltip("How quickly the flap moves back and forth while touching a game piece.")]
        [SerializeField] private float flapSpeed = 5f;

        [Tooltip("Axis the flap rotates around.")]
        [SerializeField] private JointAxis flapAxis = JointAxis.X;


        [Header("Animation Joints - Coral Intake")]
        [SerializeField] private GenericAnimationJoint[] coralTopIntakeWheels;
        [SerializeField] private GenericAnimationJoint[] coralBottomIntakeWheels;
        [SerializeField] private float coralWheelIntakeSpeed = 500f;

        [Header("Animation Joints - Algae Intake")]
        [SerializeField] private GenericAnimationJoint[] algaeTopIntakeWheels;
        [SerializeField] private GenericAnimationJoint[] algaeBottomIntakeWheels;
        [SerializeField] private float algaeWheelIntakeSpeed = 500f;

        private ClimbScorer _climbScorer;
        private bool _isScoring = false;

        [SerializeField] private Collider l1POSCollider;

        [Header("PID Constants")]
        [SerializeField] private PidConstants armPidConstants;
        [SerializeField] private PidConstants wristPidConstants;
        [SerializeField] private float pivotStep;

        private float _originalPivotMax;

        [Header("Scoring Movement Delay")]
        [Tooltip("How long the arm/wrist move before the elevator starts moving to a scoring setpoint.")]
        [SerializeField] private float scoringElevatorDelay = 0.6f;

        private Coroutine _scoringMoveRoutine;
        private NomadV2Setpoint _activeDelayedScoringSetpoint;


        // =========================================================
        // ROBOT SETPOINT ASSETS
        // =========================================================

        [Header("Robot Setpoints")]
        [SerializeField] private NomadV2Setpoint stowSetpoint;
        [SerializeField] private NomadV2Setpoint coralStowSetpoint;
        [SerializeField] private NomadV2Setpoint algaeStowSetpoint;

        [SerializeField] private NomadV2Setpoint groundCoralIntakeSetpoint;
        [SerializeField] private NomadV2Setpoint groundAlgaeIntakeSetpoint;
        [SerializeField] private NomadV2Setpoint stationCoralIntakeSetpoint;
        [SerializeField] private NomadV2Setpoint stackAlgaeIntakeSetpoint;

        [SerializeField] private NomadV2Setpoint l1Setpoint;
        [SerializeField] private NomadV2Setpoint l1VertSetpoint;
        [SerializeField] private NomadV2Setpoint l1HighSetpoint;

        [SerializeField] private NomadV2Setpoint l2Setpoint;

        [SerializeField] private NomadV2Setpoint l3Setpoint;
        [SerializeField] private NomadV2Setpoint l3BackSetpoint;

        [SerializeField] private NomadV2Setpoint l4Setpoint;
        [SerializeField] private NomadV2Setpoint l4BackSetpoint;
        [SerializeField] private NomadV2Setpoint l4BackPlaceSetpoint;

        [SerializeField] private NomadV2Setpoint lowAlgaeSetpoint;
        [SerializeField] private NomadV2Setpoint lowAlgaeBackSetpoint;

        [SerializeField] private NomadV2Setpoint highAlgaeSetpoint;
        [SerializeField] private NomadV2Setpoint highAlgaeBackSetpoint;

        [SerializeField] private NomadV2Setpoint processorSetpoint;

        [SerializeField] private NomadV2Setpoint bargeSetpoint;
        [SerializeField] private NomadV2Setpoint bargePlaceSetpoint;

        [SerializeField] private NomadV2Setpoint climbSetpoint;
        [SerializeField] private NomadV2Setpoint climbedSetpoint;


        private ReefscapeSetpoints _previousSetpoint = ReefscapeSetpoints.Stow;


        // =========================================================
        // GAME PIECE CONTROLLERS
        // =========================================================

        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;


        [Header("Game Piece Intakes")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;


        [Header("Game Piece States")]
        [SerializeField] private string currentState;

        [SerializeField] private GamePieceState coralIntakeState;
        [SerializeField] private GamePieceState coralStowState;
        [SerializeField] private GamePieceState coralBackStowState;
        [SerializeField] private GamePieceState coralFrontStowState;
        [SerializeField] private GamePieceState coralL1TargetState;

        [SerializeField] private GamePieceState algaeStowState;
        [SerializeField] private GamePieceState algaeHomeState;

        [SerializeField] private float algaeEjectForce;


        // =========================================================
        // AUDIO
        // =========================================================

        [Header("Intake Audio")]
        [SerializeField] private AudioSource intakeAudioSource;
        [SerializeField] private AudioClip intakeClip;

        [SerializeField] private AudioSource algaeStallSource;
        [SerializeField] private AudioClip algaeStallClip;


        // =========================================================
        // CURRENT TARGETS
        // =========================================================

        [Header("Target Setpoints")]
        [SerializeField] private float targetArmAngle;
        [SerializeField] private float targetWristAngle;
        [SerializeField] private float targetArmDistance;


        private bool _robotSpectialPressed;
        private bool _stationMode;


        protected override void Start()
        {
            base.Start();

            _climbScorer = GetComponent<ClimbScorer>();

            armJoint.SetPid(armPidConstants);
            wristJoint.SetPid(wristPidConstants);

            // Intake flap PID - added
            if (intakeFlap != null)
                intakeFlap.SetPid(intakeFlapPid);

            _originalPivotMax = armPidConstants.Max;


            // =====================================================
            // LOAD STOW SETPOINT
            // =====================================================

            targetArmAngle = stowSetpoint.armAngle;
            targetWristAngle = stowSetpoint.wristAngle;
            targetArmDistance = stowSetpoint.armDistance;


            // =====================================================
            // PRELOAD
            // =====================================================

            RobotGamePieceController.SetPreload(coralStowState);


            // =====================================================
            // GAME PIECE CONTROLLERS
            // =====================================================

            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _algaeController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());

            _coralController.gamePieceStates = new[] { coralIntakeState, coralStowState, coralBackStowState, coralFrontStowState, coralL1TargetState };
            _coralController.intakes.Add(coralIntake);

            _algaeController.gamePieceStates = new[] { algaeStowState, algaeHomeState };
            _algaeController.intakes.Add(algaeIntake);

            _robotSpectialPressed = false;

            _stationMode = false;


            // =====================================================
            // AUDIO
            // =====================================================

            intakeAudioSource.clip = intakeClip;
            intakeAudioSource.loop = true;
            intakeAudioSource.playOnAwake = false;

            algaeStallSource.clip = algaeStallClip;
            algaeStallSource.loop = true;
            algaeStallSource.playOnAwake = false;
        }


        private void LateUpdate()
        {
            armJoint.UpdatePid(armPidConstants);
            wristJoint.UpdatePid(wristPidConstants);

            // Intake flap PID - added
            if (intakeFlap != null)
                intakeFlap.UpdatePid(intakeFlapPid);
        }


        private void FixedUpdate()
        {
            // =====================================================
            // APPLY CURRENT MECHANISM TARGETS
            // =====================================================

            armJoint.SetTargetAngle(targetArmAngle).withAxis(JointAxis.X).flipDirection();
            wristJoint.SetTargetAngle(targetWristAngle).withAxis(JointAxis.X).flipDirection().noWrap(-30);
            elevator.SetTarget(targetArmDistance);


            // =====================================================
            // INTAKE FLAP - ADDED
            //
            // Only flaps while a game piece is ACTUALLY
            // interacting with coralIntake or algaeIntake.
            // =====================================================

            bool coralTouchingFlap = coralIntake != null && coralIntake.GamePiece != null;
            bool algaeTouchingFlap = algaeIntake != null && algaeIntake.GamePiece != null;
            bool gamePieceTouchingFlap = coralTouchingFlap || algaeTouchingFlap;

            float flapTargetAngle;

            if (gamePieceTouchingFlap)
            {
                float flapAmount = Mathf.PingPong(Time.time * flapSpeed, 1f);
                flapTargetAngle = Mathf.Lerp(flapRestAngle, flapIntakeAngle, flapAmount);
            }
            else
            {
                flapTargetAngle = flapRestAngle;
            }

            if (intakeFlap != null)
                intakeFlap.SetTargetAngle(flapTargetAngle).withAxis(flapAxis);


            // =====================================================
            // GAME PIECE INTAKE CONDITIONS
            // =====================================================

            var canIntakeCoral = _coralController.currentStateNum == 0 && IntakeAction.IsPressed() && _algaeController.currentStateNum == 0;
            var canIntakeAlgae = _algaeController.currentStateNum == 0 && IntakeAction.IsPressed() && _coralController.currentStateNum == 0;

            var realStep = pivotStep;


            // =====================================================
            // ALGAE SLIDER
            // =====================================================

            if (algaeIntake.GamePiece != null)
            {
                var localSliderSpace = algaeIntake.transform.InverseTransformPoint(algaeIntake.GamePiece.transform.position).x;
                algaeSlider.localPosition = new Vector3(-localSliderSpace, algaeSlider.localPosition.y, algaeSlider.localPosition.z);
            }


            // =====================================================
            // ARM PID MAX CONTROL
            // =====================================================

            if (Utils.WithinAngularRange(armJoint.GetSingleAxisAngle(JointAxis.X), targetArmAngle, 15f))
                armPidConstants.Max = Mathf.Max(armPidConstants.Max - realStep * Time.fixedDeltaTime, realStep);
            else
                armPidConstants.Max = Mathf.Min(armPidConstants.Max + realStep * Time.fixedDeltaTime, _originalPivotMax);


            // =====================================================
            // L1 POSITION COLLIDER
            // =====================================================

            l1POSCollider.enabled = CurrentIntakeMode == ReefscapeIntakeMode.L1;


            // =====================================================
            // READ CORAL STATE
            // =====================================================

            var readState = _coralController.GetCurrentState();

            if (readState != null)
                currentState = readState.name;

            UpdateIntakeAudio();

            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
                return;


            // =====================================================
            // ALGAE HOLD STATE
            // =====================================================

            _algaeController.SetTargetState(_algaeController.currentStateNum > 0 ? algaeHomeState : algaeStowState);

            CheckStationMode();


            // =====================================================
            // STATION MODE
            // =====================================================

            if ((_previousSetpoint == ReefscapeSetpoints.RobotSpecial && IntakeAction.IsPressed()) ||
                (_stationMode && IntakeAction.IsPressed() && CurrentSetpoint != ReefscapeSetpoints.HighAlgae &&
                 CurrentSetpoint != ReefscapeSetpoints.LowAlgae && CurrentRobotMode != ReefscapeRobotMode.Algae))
            {
                SetState(ReefscapeSetpoints.RobotSpecial);
            }


            // =====================================================
            // WHEEL ANIMATION
            // =====================================================

            if (!_isScoring)
            {
                bool intakeHeld = IntakeAction.IsPressed();

                bool coralAnimationActive = intakeHeld &&
                    (CurrentSetpoint == ReefscapeSetpoints.RobotSpecial ||
                     (CurrentSetpoint == ReefscapeSetpoints.Intake && CurrentRobotMode == ReefscapeRobotMode.Coral));

                bool algaeAnimationActive = intakeHeld &&
                    (CurrentSetpoint == ReefscapeSetpoints.Stack ||
                     CurrentSetpoint == ReefscapeSetpoints.LowAlgae ||
                     CurrentSetpoint == ReefscapeSetpoints.HighAlgae ||
                     (CurrentSetpoint == ReefscapeSetpoints.Intake && CurrentRobotMode == ReefscapeRobotMode.Algae));

                // Coral and Algae visual rollers are completely independent.
                // Using one intake will no longer make the other intake's wheels spin.
                SetCoralAnimationWheelSpeeds(coralAnimationActive ? coralWheelIntakeSpeed : 0f);
                SetAlgaeAnimationWheelSpeeds(algaeAnimationActive ? algaeWheelIntakeSpeed : 0f);

                if (!coralAnimationActive && !algaeAnimationActive)
                {
                    leftIntakeRollerJoint.ChangeAngularVelocity(0);
                    rightIntakeRollerJoint.ChangeAngularVelocity(0);
                    topIntakeRoller.ChangeAngularVelocity(0);
                }
            }


            // =====================================================
            // CLIMB AUTO STATE
            // =====================================================

            if (_climbScorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climb && climber.WingsOpen())
                SetState(ReefscapeSetpoints.Climbed);
            else if (!_climbScorer.AutoClimbTriggered && CurrentSetpoint == ReefscapeSetpoints.Climbed)
                SetState(ReefscapeSetpoints.Climb);


            // =====================================================
            // DRIVE MULTIPLIER
            // =====================================================

            if (CurrentSetpoint is ReefscapeSetpoints.Climb or ReefscapeSetpoints.Climbed)
                DriveController.SetDriveMp(0.5f);
            else if (CurrentSetpoint == ReefscapeSetpoints.Barge || LastSetpoint == ReefscapeSetpoints.Barge)
                DriveController.SetDriveMp(0.8f);
            else
                DriveController.SetDriveMp(1f);


            // =====================================================
            // SETPOINT STATE CONTROL
            // =====================================================

            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    if (_coralController.currentStateNum != 0 || _algaeController.currentStateNum != 0)
                    {
                        SetSetpoint(_coralController.currentStateNum > 0
                            ? (CurrentIntakeMode == ReefscapeIntakeMode.L1 ? l1HighSetpoint : coralStowSetpoint)
                            : algaeStowSetpoint);

                        _coralController.SetTargetState(coralStowState);
                        break;
                    }

                    SetSetpoint(stowSetpoint);
                    _coralController.SetTargetState(coralStowState);
                    break;


                case ReefscapeSetpoints.Intake:
                    if (LastSetpoint == ReefscapeSetpoints.RobotSpecial)
                    {
                        SetState(ReefscapeSetpoints.RobotSpecial);
                        break;
                    }

                    SetSetpoint(CurrentRobotMode == ReefscapeRobotMode.Coral ? groundCoralIntakeSetpoint : groundAlgaeIntakeSetpoint);

                    _coralController.SetTargetState(CurrentIntakeMode == ReefscapeIntakeMode.L1 ? coralL1TargetState : coralIntakeState);

                    _coralController.RequestIntake(coralIntake, canIntakeCoral && CurrentRobotMode == ReefscapeRobotMode.Coral);
                    _algaeController.RequestIntake(algaeIntake, canIntakeAlgae && CurrentRobotMode == ReefscapeRobotMode.Algae);

                    break;


                case ReefscapeSetpoints.Place:
                    StartCoroutine(PlaceGamePiece(LastSetpoint, readState));
                    break;


                case ReefscapeSetpoints.L1:
                    SetSetpoint(CurrentIntakeMode == ReefscapeIntakeMode.L1 ? l1Setpoint : l1VertSetpoint);
                    break;


                case ReefscapeSetpoints.Processor:
                    SetSetpoint(processorSetpoint);
                    break;


                case ReefscapeSetpoints.Stack:
                    SetSetpoint(stackAlgaeIntakeSetpoint);
                    _algaeController.RequestIntake(algaeIntake, canIntakeAlgae);
                    break;


                case ReefscapeSetpoints.L2:
                    // L2 is FRONT ONLY on 6995.
                    SetScoringSetpoint(l2Setpoint);
                    _coralController.SetTargetState(coralFrontStowState);
                    break;


                case ReefscapeSetpoints.LowAlgae:
                    SetSetpoint(FacingReef ? lowAlgaeSetpoint : lowAlgaeBackSetpoint);
                    _algaeController.RequestIntake(algaeIntake, canIntakeAlgae);
                    break;


                case ReefscapeSetpoints.L3:
                    SetScoringSetpoint(FacingReef ? l3Setpoint : l3BackSetpoint);
                    _coralController.SetTargetState(FacingReef ? coralFrontStowState : coralBackStowState);
                    break;


                case ReefscapeSetpoints.HighAlgae:
                    SetSetpoint(FacingReef ? highAlgaeSetpoint : highAlgaeBackSetpoint);
                    _algaeController.RequestIntake(algaeIntake, canIntakeAlgae);
                    break;


                case ReefscapeSetpoints.L4:
                    SetScoringSetpoint(FacingReef ? l4Setpoint : l4BackSetpoint);
                    _coralController.SetTargetState(FacingReef ? coralFrontStowState : coralBackStowState);
                    break;


                case ReefscapeSetpoints.Barge:
                    SetSetpoint(bargeSetpoint);
                    break;


                case ReefscapeSetpoints.RobotSpecial:
                    SetSetpoint(stationCoralIntakeSetpoint);
                    _coralController.SetTargetState(coralStowState);
                    _coralController.RequestIntake(coralIntake, canIntakeCoral);
                    break;


                case ReefscapeSetpoints.Climb:
                    SetSetpoint(climbSetpoint);
                    climber.Climb();
                    break;


                case ReefscapeSetpoints.Climbed:
                    StartCoroutine(RotateArmFirst(climbedSetpoint));
                    climber.NotClimbing();
                    _coralController.SetTargetState(coralStowState);
                    break;


                default:
                    throw new System.ArgumentOutOfRangeException();
            }


            // =====================================================
            // L1 INTAKE / RAYCAST LOGIC
            // =====================================================

            if (CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                _coralController.MoveIntake(coralIntake, coralL1TargetState.stateTarget);
                _coralController.SetTargetState(coralL1TargetState);

                if (leftIntakeRollerJoint.gameObject.activeSelf)
                {
                    leftIntakeRollerJoint.gameObject.SetActive(false);
                    rightIntakeRollerJoint.gameObject.SetActive(false);
                }
            }
            else
            {
                _coralController.MoveIntake(coralIntake, coralIntakeState.stateTarget);

                if (!leftIntakeRollerJoint.gameObject.activeSelf)
                {
                    leftIntakeRollerJoint.gameObject.SetActive(true);
                    rightIntakeRollerJoint.gameObject.SetActive(true);
                }

                var rayDirection = coralIntakeState.stateTarget.forward;
                var distance = 0.0254f * 5f;
                var coralMask = LayerMask.GetMask("Coral");

                var coralRight = Physics.Raycast(rightIntakeSensor.position, rayDirection, distance, coralMask);
                var coralLeft = Physics.Raycast(leftIntakeSensor.position, rayDirection, distance, coralMask);

                if (IntakeAction.IsPressed() &&
                    CurrentSetpoint != ReefscapeSetpoints.LowAlgae &&
                    CurrentSetpoint != ReefscapeSetpoints.HighAlgae)
                {
                    if (coralRight && coralLeft)
                    {
                        leftIntakeRollerJoint.ChangeAngularVelocity(8000);
                        rightIntakeRollerJoint.ChangeAngularVelocity(8000);
                    }
                }
            }

            _previousSetpoint = CurrentSetpoint;
        }


        // =========================================================
        // PLACE GAME PIECE
        // =========================================================

        private IEnumerator PlaceGamePiece(ReefscapeSetpoints lastSetpoint, GamePieceState readState)
        {
            _isScoring = true;

            // Only animate the mechanism that is actually scoring.
            StopAllAnimationWheels();

            bool scoringAlgae =
                lastSetpoint == ReefscapeSetpoints.LowAlgae ||
                lastSetpoint == ReefscapeSetpoints.HighAlgae ||
                lastSetpoint == ReefscapeSetpoints.Processor ||
                lastSetpoint == ReefscapeSetpoints.Barge ||
                (CurrentRobotMode == ReefscapeRobotMode.Algae && _algaeController.HasPiece());

            if (scoringAlgae)
            {
                float algaeSpeed = FacingReef ? algaeWheelIntakeSpeed : -algaeWheelIntakeSpeed;
                SetAlgaeAnimationWheelSpeeds(algaeSpeed);
            }
            else
            {
                float coralSpeed = FacingReef ? coralWheelIntakeSpeed : -coralWheelIntakeSpeed;
                SetCoralAnimationWheelSpeeds(coralSpeed);
            }


            // =====================================================
            // BARGE PLACE
            // =====================================================

            if (lastSetpoint == ReefscapeSetpoints.Barge)
            {
                targetArmAngle = bargePlaceSetpoint.armAngle;
                targetWristAngle = bargePlaceSetpoint.wristAngle;
                targetArmDistance = bargePlaceSetpoint.armDistance;

                yield return new WaitForSeconds(0.075f);
            }


            // =====================================================
            // L1 PLACE
            // =====================================================

            else if (lastSetpoint == ReefscapeSetpoints.L1 && CurrentIntakeMode != ReefscapeIntakeMode.L1)
            {
                leftIntakeRollerJoint.ChangeAngularVelocity(1000);
                rightIntakeRollerJoint.ChangeAngularVelocity(-1000);

                topIntakeRoller.flipVelocity();
            }


            // =====================================================
            // BACK SCORING
            // =====================================================

            else if (lastSetpoint != ReefscapeSetpoints.Processor && !FacingReef)
            {
                leftIntakeRollerJoint.flipVelocity();
                rightIntakeRollerJoint.flipVelocity();
                topIntakeRoller.flipVelocity();
            }


            // =====================================================
            // RELEASE FORCE
            // =====================================================

            Vector3 force;

            if (CurrentIntakeMode == ReefscapeIntakeMode.L1 ||
                (readState != null && readState.stateNum == coralL1TargetState.stateNum))
            {
                force = new Vector3(1f, 0f, 0f);
            }
            else
            {
                force = FacingReef ? new Vector3(0f, 0f, -5f) : new Vector3(0f, 0f, 5f);

                if (lastSetpoint == ReefscapeSetpoints.L1)
                    force = new Vector3(0f, 0f, 4f);
            }

            _coralController.ReleaseGamePieceWithForce(force);
            _algaeController.ReleaseGamePieceWithForce(new Vector3(0f, algaeEjectForce, 0f));


            // =====================================================
            // L4 BACK PLACE
            // =====================================================

            if (lastSetpoint == ReefscapeSetpoints.L4 && !FacingReef)
            {
                yield return new WaitForSeconds(0.05f);

                targetArmAngle = l4BackPlaceSetpoint.armAngle;
                targetWristAngle = l4BackPlaceSetpoint.wristAngle;
                targetArmDistance = l4BackPlaceSetpoint.armDistance;
            }


            // =====================================================
            // WAIT FOR PIECES TO RELEASE
            // =====================================================

            float timer = 0f;

            while ((_coralController.currentStateNum != 0 || _algaeController.currentStateNum != 0) && timer < 0.5f)
            {
                timer += Time.deltaTime;
                yield return null;
            }


            // =====================================================
            // STOP WHEEL ANIMATION
            // =====================================================

            StopAllAnimationWheels();

            _isScoring = false;
        }


        // =========================================================
        // STATION MODE
        // =========================================================

        private void CheckStationMode()
        {
            if (RobotSpecialAction.IsPressed() &&
                !_robotSpectialPressed &&
                BaseGameManager.Instance.RobotState == RobotState.Enabled)
            {
                _stationMode = !_stationMode;
            }

            CurrentCoralStationMode.DropType = _stationMode ? DropType.Station : DropType.Ground;
            _robotSpectialPressed = RobotSpecialAction.IsPressed();
        }


        // =========================================================
        // AUDIO
        // =========================================================

        private void UpdateIntakeAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (intakeAudioSource.isPlaying || algaeStallSource.isPlaying)
                {
                    intakeAudioSource.Stop();
                    algaeStallSource.Stop();
                }

                return;
            }

            if ((IntakeAction.IsPressed() || OuttakeAction.IsPressed() || CurrentSetpoint == ReefscapeSetpoints.Climb) &&
                !intakeAudioSource.isPlaying)
            {
                intakeAudioSource.Play();
            }
            else if (!IntakeAction.IsPressed() &&
                     !OuttakeAction.IsPressed() &&
                     CurrentSetpoint != ReefscapeSetpoints.Climb &&
                     intakeAudioSource.isPlaying)
            {
                intakeAudioSource.Stop();
            }

            if (RobotGamePieceController.GetPieceByName("Algae").currentStateNum > 0 && !algaeStallSource.isPlaying)
                algaeStallSource.Play();
            else if (RobotGamePieceController.GetPieceByName("Algae").currentStateNum == 0 && algaeStallSource.isPlaying)
                algaeStallSource.Stop();
        }


        // =========================================================
        // CLIMBED SEQUENCE
        // =========================================================

        private IEnumerator RotateArmFirst(NomadV2Setpoint setpoint)
        {
            targetArmAngle = setpoint.armAngle;
            targetWristAngle = setpoint.wristAngle;

            yield return new WaitForSeconds(0.65f);

            targetArmDistance = setpoint.armDistance;
        }


        // =========================================================
        // VISUAL INTAKE WHEELS
        // =========================================================

        private void SetCoralAnimationWheelSpeeds(float speed)
        {
            if (coralTopIntakeWheels != null)
            {
                foreach (var wheel in coralTopIntakeWheels)
                    if (wheel != null) wheel.VelocityRoller(speed).useAxis(JointAxis.X);
            }

            if (coralBottomIntakeWheels != null)
            {
                foreach (var wheel in coralBottomIntakeWheels)
                    if (wheel != null) wheel.VelocityRoller(-speed).useAxis(JointAxis.X);
            }
        }

        private void SetAlgaeAnimationWheelSpeeds(float speed)
        {
            if (algaeTopIntakeWheels != null)
            {
                foreach (var wheel in algaeTopIntakeWheels)
                    if (wheel != null) wheel.VelocityRoller(speed).useAxis(JointAxis.X);
            }

            if (algaeBottomIntakeWheels != null)
            {
                foreach (var wheel in algaeBottomIntakeWheels)
                    if (wheel != null) wheel.VelocityRoller(-speed).useAxis(JointAxis.X);
            }
        }

        private void StopAllAnimationWheels()
        {
            SetCoralAnimationWheelSpeeds(0f);
            SetAlgaeAnimationWheelSpeeds(0f);
        }


        // =========================================================
        // LOAD SETPOINT ASSET
        // =========================================================

        // Used by separate 6995 helper scripts without accessing protected base fields.
        public ReefscapeRobotMode GetCurrentRobotModeForAlign()
        {
            return CurrentRobotMode;
        }

        // Lets separate helper auto-align scripts use the exact same
        // front/back decision as the main NomadV2 mechanism logic.
        public bool GetFacingReefForAlign()
        {
            return FacingReef;
        }


        private void SetScoringSetpoint(NomadV2Setpoint setpoint)
        {
            if (setpoint == null)
                return;

            // FixedUpdate runs every physics frame, so don't restart the same delay every frame.
            if (_activeDelayedScoringSetpoint == setpoint)
                return;

            CancelScoringMove();

            _activeDelayedScoringSetpoint = setpoint;
            _scoringMoveRoutine = StartCoroutine(MoveArmThenElevator(setpoint));
        }

        private IEnumerator MoveArmThenElevator(NomadV2Setpoint setpoint)
        {
            // Move the arm and wrist first while keeping the elevator at its current target.
            targetArmAngle = setpoint.armAngle;
            targetWristAngle = setpoint.wristAngle;

            yield return new WaitForSeconds(scoringElevatorDelay);

            // After the delay, allow the elevator to move to the scoring height.
            targetArmDistance = setpoint.armDistance;

            _scoringMoveRoutine = null;
        }

        private void CancelScoringMove()
        {
            if (_scoringMoveRoutine != null)
            {
                StopCoroutine(_scoringMoveRoutine);
                _scoringMoveRoutine = null;
            }

            _activeDelayedScoringSetpoint = null;
        }

        private void SetSetpoint(NomadV2Setpoint setpoint)
        {
            if (setpoint == null)
                return;

            // Any normal/non-delayed setpoint cancels a pending scoring elevator move.
            CancelScoringMove();

            targetArmAngle = setpoint.armAngle;
            targetWristAngle = setpoint.wristAngle;
            targetArmDistance = setpoint.armDistance;
        }
    }
}