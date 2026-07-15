using System;
using System.Collections;
using Games.Reefscape.Enums;
using Games.Reefscape.FieldScripts;
using Games.Reefscape.GamePieceSystem;
using Games.Reefscape.Robots;
using MoSimCore.BaseClasses.GameManagement;
using MoSimCore.Enums;
using MoSimLib;
using RobotFramework.Components;
using RobotFramework.Controllers.GamePieceSystem;
using RobotFramework.Controllers.PidSystems;
using RobotFramework.Enums;
using RobotFramework.GamePieceSystem;
using UnityEngine;

namespace Prefabs.Reefscape.Robots.Mods.NYPowerhousePack._694
{
    public class StuyPulse: ReefscapeRobotBase
    {
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint eeArm;
        [SerializeField] private GenericJoint froggy;
        [SerializeField] private GenericJoint climbPivot1;
        [SerializeField] private GenericJoint climbPivot2;

        
        [Header("PID Constants")]
        [SerializeField] private PidConstants eeArmPid;
        [SerializeField] private PidConstants froggyPid;
        [SerializeField] private PidConstants climbPivotsPid;

        
        [Header("Setpoints")]
        [SerializeField] private StuyPulseSetpoint stow;
        [SerializeField] private StuyPulseSetpoint intakeFunnel;
        [SerializeField] private StuyPulseSetpoint eeL1;
        [SerializeField] private StuyPulseSetpoint frontL2;
        [SerializeField] private StuyPulseSetpoint backL2;
        [SerializeField] private StuyPulseSetpoint frontL3;
        [SerializeField] private StuyPulseSetpoint backL3;
        [SerializeField] private StuyPulseSetpoint frontL4;
        [SerializeField] private StuyPulseSetpoint backL4;
        [SerializeField] private StuyPulseSetpoint backL4Scored;

        [SerializeField] private StuyPulseSetpoint lollipopIntake;
        [SerializeField] private StuyPulseSetpoint frontLowAlgae;
        [SerializeField] private StuyPulseSetpoint frontHighAlgae;
        [SerializeField] private StuyPulseSetpoint backLowAlgae;
        [SerializeField] private StuyPulseSetpoint backHighAlgae;
        [SerializeField] private StuyPulseSetpoint bargePrep;
        [SerializeField] private StuyPulseSetpoint bargePlace;
        [SerializeField] private StuyPulseSetpoint process;

        [SerializeField] private StuyPulseSetpoint froggyCoral;
        [SerializeField] private StuyPulseSetpoint froggyAlgae;
        [SerializeField] private StuyPulseSetpoint froggyLollipop;
        [SerializeField] private StuyPulseSetpoint froggyCoralPlace;
        [SerializeField] private StuyPulseSetpoint froggyAlgaeProcess;

        [SerializeField] private StuyPulseSetpoint climbStow;
        [SerializeField] private StuyPulseSetpoint climbPrep;
        [SerializeField] private StuyPulseSetpoint climbClimb;

        [Header("Intakes and Stows")] 
        [SerializeField] private ReefscapeGamePieceIntake funnelCoralIntake;
        [SerializeField] private ReefscapeGamePieceIntake shooterAlgaeIntake;
        [SerializeField] private ReefscapeGamePieceIntake froggyCoralIntake;
        [SerializeField] private ReefscapeGamePieceIntake froggyAlgaeIntake;

        [SerializeField] private GamePieceState shooterCoralStowState;
        [SerializeField] private GamePieceState shooterAlgaeStowState;
        
        [SerializeField] private GamePieceState froggyCoralStowState;
        [SerializeField] private GamePieceState froggyAlgaeStowState;

        [Header("Auto Align Offsets")] 
        [SerializeField] private AutoAlignOffset l1offset;
        [SerializeField] private AutoAlignOffset frontLeftOffset;
        [SerializeField] private AutoAlignOffset frontRightOffset;
        [SerializeField] private AutoAlignOffset backLeftOffset;
        [SerializeField] private AutoAlignOffset backRightOffset;
        [SerializeField] private AutoAlignOffset frontLeftL4Offset;
        [SerializeField] private AutoAlignOffset frontRightL4Offset;
        [SerializeField] private AutoAlignOffset backLeftL4Offset;
        [SerializeField] private AutoAlignOffset backRightL4Offset;
        
        [SerializeField] private AutoAlignOffset L1FroggyOffset;

        [Header("Froggy Shit")]
        [SerializeField] private Transform forggyCoralTarget;
        [SerializeField] private Transform frogyCoralSlid;
        [SerializeField] private Transform froggyAlgaeTarger;
        [SerializeField] private Transform froggyAlgaeSlider;
        
        [Header("Rollers n Other Stuff ig")]
        [SerializeField] private GenericRoller[] froggyRollers;
        [SerializeField] private GenericRoller[] funnelRollers;

        [Header("Colliders n shit")]
        [SerializeField] private CapsuleCollider[] froggyRollerColliders;
        [SerializeField] private BoxCollider[] collidersToDisableForFroggyCoralScoring;
        [SerializeField] private MeshCollider[] shooterCollidersForAlgae;
        
        [Header("Audio Stuff")]
        [SerializeField] private AudioSource funnelAudioSource;
        [SerializeField] private AudioClip funnelAudioClip;
        [SerializeField] private AudioSource endEffectorAudioSource;
        [SerializeField] private AudioClip endEffectorAudioClip;
        [SerializeField] private AudioSource froggyAudioSource;
        [SerializeField] private AudioClip froggyAudioClip;
        
        [Header("Animation Wheel Sets")]
        [SerializeField] private GenericAnimationJoint[] shooterWheelsTop;
        [SerializeField] private GenericAnimationJoint[] shooterBottomWheels;
        [SerializeField] private float shooterAnimationWheelSpeeds = 300;
        
        [SerializeField] private GenericAnimationJoint[] froggyGreenRollerWheels;
        [SerializeField] private GenericAnimationJoint[] froggyOrangeRollerWheels;
        [SerializeField] private float froggyAnimationWheelSpeeds = 150;

        [SerializeField] private FroggyState frogState = FroggyState.Stow;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;

        private float _elevatorTargetHeight;
        private float _eeArmTargetAngle;
        private float _froggyTargetAngle;
        private float _climbPivot1TargetAngle;
        private float _climbPivot2TargetAngle;

        private ReefscapeAutoAlign align;

        private bool stillInPlaceState = false;

        private bool froggyLolli = false;
        
        private float _funnelWheels;
        private float _froggyWheels;
        private bool _isIntaking;
        private float _outtakeAudioUntil = 0f;
        private float _froggyOuttakeAudioUntil = 0f;

        private float froggyWheelSpeeds;
        private float shooterWheelSpeeds;
        
        private Vector3 _blueReef;
        private Vector3 _redReef;
        
        protected override void Start()
        {
            base.Start();
            SetRobotMode(ReefscapeRobotMode.Coral);
            
            eeArm.SetPid(eeArmPid);
            froggy.SetPid(froggyPid);
            climbPivot1.SetPid(climbPivotsPid);
            climbPivot2.SetPid(climbPivotsPid);

            _elevatorTargetHeight = 0;
            _eeArmTargetAngle = 0;
            _froggyTargetAngle = 0;
            _climbPivot1TargetAngle = 0;
            _climbPivot2TargetAngle = 0;
            
            RobotGamePieceController.SetPreload(shooterCoralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _algaeController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());

            _coralController.gamePieceStates = new[]
            {
                shooterCoralStowState, 
                froggyCoralStowState
            };
            _coralController.intakes.Add(funnelCoralIntake);
            _coralController.intakes.Add(froggyCoralIntake);

            _algaeController.gamePieceStates = new[]
            {
                shooterAlgaeStowState,
                froggyAlgaeStowState
            };
            _algaeController.intakes.Add(shooterAlgaeIntake);
            _algaeController.intakes.Add(froggyAlgaeIntake);
            
            align = gameObject.GetComponent<ReefscapeAutoAlign>();
            
            _blueReef = GameObject.Find("BlueReef").transform.position;
            _redReef = GameObject.Find("RedReef").transform.position;
            
            if (funnelAudioSource != null && funnelAudioClip != null)
            {
                funnelAudioSource.clip = funnelAudioClip;
                funnelAudioSource.volume = 0.2f;
                funnelAudioSource.loop = true;
                funnelAudioSource.Stop();
            }

            if (endEffectorAudioSource != null && endEffectorAudioClip != null)
            {
                endEffectorAudioSource.clip = endEffectorAudioClip;
                endEffectorAudioSource.volume = 0.2f;
                endEffectorAudioSource.loop = true;
                endEffectorAudioSource.Stop();
            }

            if (froggyAudioSource != null && froggyAudioClip != null)
            {
                froggyAudioSource.clip = froggyAudioClip;
                froggyAudioSource.volume = 0.2f;
                froggyAudioSource.loop = true;
                froggyAudioSource.Stop();
            }
        }

        private void SetSetpoint(StuyPulseSetpoint setpoint)
        {
            _elevatorTargetHeight = setpoint.elevatorHeight;
            _eeArmTargetAngle = setpoint.eeArmAngle;
            _froggyTargetAngle = setpoint.froggyAngle;
            _climbPivot1TargetAngle = setpoint.climbPivot1Angle;
            _climbPivot2TargetAngle = setpoint.climbPivot2Angle;
        }

        private void SetWheelSpeeds(float fog, float shot)
        {
            froggyWheelSpeeds = -fog;
            shooterWheelSpeeds = shot;
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            eeArm.SetTargetAngle(_eeArmTargetAngle).withAxis(JointAxis.X).noWrap(20);
            froggy.SetTargetAngle(_froggyTargetAngle).withAxis(JointAxis.X).noWrap(-110);
            climbPivot1.SetTargetAngle(_climbPivot1TargetAngle).withAxis(JointAxis.X).noWrap(140);
            climbPivot2.SetTargetAngle(-1 * _climbPivot2TargetAngle).withAxis(JointAxis.X).noWrap(-140);

            // Audio logic: Mutually exclusive - froggy mode plays only froggy, station mode plays only funnel
            bool hasCoral = _coralController.HasPiece();
            bool hasAlgae = _algaeController.HasPiece();
            bool isFroggyMode = CurrentIntakeMode == ReefscapeIntakeMode.L1;
            bool isStationMode = !isFroggyMode;
            
            // Froggy audio: plays when in L1 mode and (intaking OR outtaking)
            bool froggyIntaking = isFroggyMode && Mathf.Abs(_froggyWheels) > 1e-6;
            bool froggyOuttaking = Time.time < _froggyOuttakeAudioUntil;
            bool shouldPlayFroggyAudio = (froggyIntaking || froggyOuttaking) && isFroggyMode;
            
            // Funnel audio: plays ONLY when in station mode and (intaking OR outtaking)
            // NEVER plays in froggy mode
            bool funnelIntaking = isStationMode && IntakeAction.IsPressed() && !hasCoral && !hasAlgae;
            bool funnelOuttaking = isStationMode && Time.time < _outtakeAudioUntil;
            bool shouldPlayFunnelAudio = (funnelIntaking || funnelOuttaking) && isStationMode && !isFroggyMode;
            
            // Play froggy audio (only in froggy mode)
            if (shouldPlayFroggyAudio)
            {
                if (froggyAudioSource?.isPlaying != true) froggyAudioSource?.Play();
            }
            else
            {
                froggyAudioSource?.Stop();
            }
            
            // Play funnel audio (only in station mode, NEVER in froggy mode)
            if (shouldPlayFunnelAudio && isStationMode)
            {
                if (funnelAudioSource?.isPlaying != true) funnelAudioSource?.Play();
            }
            else
            {
                funnelAudioSource?.Stop();
            }

            // Update end effector audio - play when intaking and don't have piece
            bool shouldPlayEndEffectorAudio = _isIntaking && !hasCoral && !hasAlgae;
            if (shouldPlayEndEffectorAudio)
            {
                if (endEffectorAudioSource?.isPlaying != true) endEffectorAudioSource?.Play();
            }
            else
            {
                endEffectorAudioSource?.Stop();
            }

            foreach (var joint in froggyOrangeRollerWheels)
            {
                joint.VelocityRoller(froggyWheelSpeeds);
            }

            foreach (var joint in froggyGreenRollerWheels)
            {
                joint.VelocityRoller(-froggyWheelSpeeds);
            }

            foreach (var joint in shooterWheelsTop)
            {
                joint.VelocityRoller(shooterWheelSpeeds);
            }

            foreach (var joint in shooterBottomWheels)
            {
                joint.VelocityRoller(-shooterWheelSpeeds);
            }
        }

        private void LateUpdate()
        {
            eeArm.UpdatePid(eeArmPid);
            froggy.UpdatePid(froggyPid);
            climbPivot1.UpdatePid(climbPivotsPid);
            climbPivot2.UpdatePid(climbPivotsPid);
        }

        private void SetAlignOffsets(AutoAlignOffset alignment)
        {
            align.offset = new Vector3(alignment.xOffset, alignment.yOffset, alignment.zOffset);
            align.rotation = alignment.Rotation;
        }

        private void AutoAlignnnn()
        {
            if (_coralController.currentStateNum == froggyCoralStowState.stateNum && _coralController.atTarget)
            {
                align.enableBackwardsAlign = false;
                SetAlignOffsets(l1offset);
                return;
            }
            
            if (_coralController.atTarget && _coralController.currentStateNum == shooterCoralStowState.stateNum) align.enableBackwardsAlign = true;
            
            if (AutoAlignLeftAction.IsPressed() && IsFacingReef(GetClosestReef()) && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(CurrentSetpoint == ReefscapeSetpoints.L4 ? frontLeftL4Offset : frontLeftOffset);
            }
            else if (AutoAlignRightAction.IsPressed() && IsFacingReef(GetClosestReef()) && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(CurrentSetpoint == ReefscapeSetpoints.L4 ? frontRightL4Offset : frontRightOffset);
            }
            else if (AutoAlignLeftAction.IsPressed() && !IsFacingReef(GetClosestReef()) && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(CurrentSetpoint == ReefscapeSetpoints.L4 ? backLeftL4Offset : backLeftOffset);
            }
            else if (AutoAlignRightAction.IsPressed() && !IsFacingReef(GetClosestReef()) && CurrentSetpoint !=  ReefscapeSetpoints.Place)
            {
                SetAlignOffsets(CurrentSetpoint == ReefscapeSetpoints.L4 ? backRightL4Offset : backRightOffset);
            }
        }

        private void RunFroggyWheels()
        {
            switch (frogState)
            {
                case FroggyState.Stow:
                    froggyRollers[0].stopAngularVelocity();
                    froggyRollers[1].stopAngularVelocity();
                    break;
                case FroggyState.CoralIntake:
                    froggyRollers[0].SetAngularVelocity(1000);
                    froggyRollers[1].SetAngularVelocity(-6000);
                    break;
                case FroggyState.CoralOuttake:
                    froggyRollers[0].SetAngularVelocity(-2000);
                    froggyRollers[1].SetAngularVelocity(2000);
                    break;
                case FroggyState.AlgaeIntake:
                    froggyRollers[0].SetAngularVelocity(-5000);
                    froggyRollers[1].SetAngularVelocity(0);
                    break;
                case FroggyState.AlgaeOuttake:
                    froggyRollers[0].SetAngularVelocity(0);
                    froggyRollers[1].SetAngularVelocity(-1000);
                    break;
            }
        }

        private IEnumerator ScoreFroggyL1()
        {
            SetWheelSpeeds(-froggyAnimationWheelSpeeds, 0);
            frogState = FroggyState.CoralOuttake;
            foreach (var col in collidersToDisableForFroggyCoralScoring)
            {
                col.enabled = false;
            }
            _coralController.ReleaseGamePieceWithForce(new Vector3(0, 1.5f, 0));
            
            yield return new WaitForSeconds(1f);
            
            foreach (var col in collidersToDisableForFroggyCoralScoring)
            {
                col.enabled = true;
            }
            frogState = FroggyState.Stow;
            SetWheelSpeeds(0, 0);
        }

        private void PlacePiece()
        {
            // Start outtake audio timer based on mode
            // Reset the OTHER mode's timer to prevent cross-mode audio
            if (CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                _froggyOuttakeAudioUntil = Time.time + 0.35f;
                _outtakeAudioUntil = 0f; // Reset station audio timer
            }
            else
            {
                _outtakeAudioUntil = Time.time + 0.35f;
                _froggyOuttakeAudioUntil = 0f; // Reset froggy audio timer
            }
            
            if ((CurrentRobotMode == ReefscapeRobotMode.Coral || !_algaeController.atTarget) && LastSetpoint != ReefscapeSetpoints.L2 && LastSetpoint != ReefscapeSetpoints.L3 && LastSetpoint != ReefscapeSetpoints.L4 && _coralController.HasPiece() && !(_coralController.currentStateNum == shooterCoralStowState.stateNum && _coralController.atTarget))
            {
                StartCoroutine(ScoreFroggyL1());
            }
            else if ((CurrentRobotMode == ReefscapeRobotMode.Algae || !_coralController.atTarget) && _algaeController.HasPiece())
            {
                if (_algaeController.currentStateNum == shooterAlgaeStowState.stateNum && LastSetpoint == ReefscapeSetpoints.Barge)
                {
                    frogState = FroggyState.Stow;
                    foreach (var col in shooterCollidersForAlgae)
                    {
                        col.enabled = false;
                    }
                    _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3.3f, 7.6f));
                    SetWheelSpeeds(0, shooterAnimationWheelSpeeds*1.5f);
                } else if (_algaeController.currentStateNum == shooterAlgaeStowState.stateNum && LastSetpoint == ReefscapeSetpoints.Processor)
                {
                    frogState = FroggyState.Stow;
                    foreach (var col in shooterCollidersForAlgae)
                    {
                        col.enabled = false;
                    }
                    _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3, 0));
                    SetWheelSpeeds(0, shooterAnimationWheelSpeeds/.75f);
                }
                else
                {
                    foreach (var col in shooterCollidersForAlgae)
                    {
                        col.enabled = false;
                    }

                    if (_algaeController.currentStateNum == froggyAlgaeStowState.stateNum)
                    {
                        frogState = FroggyState.AlgaeOuttake;
                    }
                    _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 3, 0));
                    SetWheelSpeeds(froggyAnimationWheelSpeeds, 0);
                    frogState = FroggyState.Stow;
                }
            }
            else if ((CurrentRobotMode == ReefscapeRobotMode.Coral || !_algaeController.atTarget) && LastSetpoint == ReefscapeSetpoints.L4)
            {
                frogState = FroggyState.Stow;
                _coralController.ReleaseGamePieceWithForce(IsFacingReef(GetClosestReef())
                                                            ? new Vector3(0, 0, -6)
                                                            : new Vector3(0, 0, 5));
                SetWheelSpeeds(0, IsFacingReef(GetClosestReef()) ? -shooterAnimationWheelSpeeds : shooterAnimationWheelSpeeds);
            }
            else if ((CurrentRobotMode == ReefscapeRobotMode.Coral || _algaeController.atTarget) && LastSetpoint == ReefscapeSetpoints.L1 && CurrentIntakeMode == ReefscapeIntakeMode.Normal)
            {
                frogState = FroggyState.Stow;
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 3.5f), 0.2f, .9f);
                SetWheelSpeeds(0, shooterAnimationWheelSpeeds);
            }
            else if (CurrentRobotMode == ReefscapeRobotMode.Coral || !_algaeController.atTarget)
            {
                frogState = FroggyState.Stow;
                _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, 5));
                SetWheelSpeeds(0, shooterAnimationWheelSpeeds);
            }

            stillInPlaceState = true;
        }

        private void FixedUpdate()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                funnelAudioSource?.Stop();
                endEffectorAudioSource?.Stop();
                froggyAudioSource?.Stop();
                return;
            }
            bool hasAlgae = _algaeController.HasPiece();
            bool hasCoral = _coralController.HasPiece();
            
            bool shooterHasAlgae = (_algaeController.currentStateNum == shooterAlgaeStowState.stateNum && _algaeController.atTarget);
            bool shooterHasCoral = (_coralController.currentStateNum == shooterCoralStowState.stateNum && _coralController.atTarget);

            if (CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                foreach (var roller in funnelRollers)
                {
                    roller.flipVelocity();
                }
            }

            if (!OuttakeAction.IsPressed() && !IntakeAction.IsPressed())
            {
                SetWheelSpeeds(0,0);
            }

            // Track if we're actively intaking (requesting intake and don't have piece)
            _isIntaking = false;
            
            // Track funnel wheel speeds
            _funnelWheels = 0f;
            if (IntakeAction.IsPressed() && !_coralController.HasPiece() && CurrentRobotMode == ReefscapeRobotMode.Coral)
            {
                _funnelWheels = 900f; // Default funnel speed
                _isIntaking = true;
            }
            
            // Track froggy wheel speeds - reset to 0, will be set in switch cases if needed
            _froggyWheels = 0f;
            // Set froggy wheels when intaking in L1 mode (IntakeAction pressed)
            if (CurrentIntakeMode == ReefscapeIntakeMode.L1 && IntakeAction.IsPressed())
            {
                if (!hasCoral && CurrentRobotMode == ReefscapeRobotMode.Coral)
                {
                    _froggyWheels = 2000f;
                }
                else if (!hasAlgae && CurrentRobotMode == ReefscapeRobotMode.Algae)
                {
                    _froggyWheels = 6000f;
                }
            }

            if (shooterHasCoral)
            {
                _algaeController.RequestIntake(shooterAlgaeIntake, false);
            } else if (shooterHasAlgae)
            {
                _coralController.RequestIntake(funnelCoralIntake, false);
            }
            
            if (froggyCoralIntake.GamePiece != null)
            {
                var localSliderSpaceZ = forggyCoralTarget.transform.InverseTransformPoint(froggyCoralIntake.GamePiece.transform.position).z;
                frogyCoralSlid.localPosition = new Vector3(0, 0, localSliderSpaceZ);
            }
            
            if (froggyAlgaeIntake.GamePiece != null)
            {
                var localSliderSpaceX = froggyAlgaeTarger.transform.InverseTransformPoint(froggyAlgaeIntake.GamePiece.transform.position).x;
                froggyAlgaeSlider.localPosition = new Vector3(localSliderSpaceX, 0, 0);
            }

            if (CurrentSetpoint != ReefscapeSetpoints.Place || RobotModeToggleAction.IsPressed())
            {
                stillInPlaceState = false;
            }

            if (CurrentIntakeMode == ReefscapeIntakeMode.L1)
            {
                CurrentCoralStationMode.DropType = DropType.Ground;
            }
            else
            {
                CurrentCoralStationMode.DropType = DropType.Station;
            }

            if (LastSetpoint == ReefscapeSetpoints.Intake && CurrentIntakeMode == ReefscapeIntakeMode.L1 && !hasCoral && CurrentRobotMode == ReefscapeRobotMode.Coral)
            {
                _coralController.SetTargetState(froggyCoralStowState);
                _coralController.RequestIntake(froggyCoralIntake, true);
                _coralController.RequestIntake(funnelCoralIntake, false);
                _isIntaking = true;
            }

            if (LastSetpoint == ReefscapeSetpoints.Place)
            {
                frogState = FroggyState.Stow;
            }

            if (LastSetpoint == ReefscapeSetpoints.Place && CurrentSetpoint == ReefscapeSetpoints.Stow)
            {
                foreach (var col in collidersToDisableForFroggyCoralScoring)
                {
                    col.enabled = true;
                }
            }
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    if (((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8)) SetSetpoint(stow);
                    frogState = FroggyState.Stow;
                    bool stowIntaking = CurrentIntakeMode != ReefscapeIntakeMode.L1 && IntakeAction.IsPressed() && !shooterHasCoral && !shooterHasAlgae;
                    _coralController.RequestIntake(funnelCoralIntake, stowIntaking && SuperstructureAtSetpoint(stow));
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    if (stowIntaking && !hasCoral)
                    {
                        _isIntaking = true;
                    }
                    foreach (var col in shooterCollidersForAlgae)
                    {
                        col.enabled = true;
                    }
                    foreach (var col in froggyRollerColliders)
                    {
                        col.enabled = true;
                    }
                    
                    SetWheelSpeeds(0, shooterHasCoral || shooterHasAlgae ? 0 : shooterAnimationWheelSpeeds);
                    break;
                case ReefscapeSetpoints.Intake:

                    if (CurrentIntakeMode == ReefscapeIntakeMode.L1 && !hasCoral && CurrentRobotMode == ReefscapeRobotMode.Coral)
                    {
                        SetSetpoint(froggyCoral);
                        frogState = FroggyState.CoralIntake;
                        _froggyWheels = 2000f;
                        _coralController.SetTargetState(froggyCoralStowState);
                        _coralController.RequestIntake(froggyCoralIntake);
                        _coralController.RequestIntake(funnelCoralIntake, false);
                        _isIntaking = true;
                        SetWheelSpeeds(froggyAnimationWheelSpeeds, 0);
                    }
                    else if (CurrentRobotMode == ReefscapeRobotMode.Coral && !hasCoral && !shooterHasAlgae && CurrentIntakeMode != ReefscapeIntakeMode.L1)
                    {
                        if (((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8)) SetSetpoint(intakeFunnel);
                        frogState = FroggyState.Stow;
                        _coralController.SetTargetState(shooterCoralStowState);
                        _coralController.RequestIntake(funnelCoralIntake, SuperstructureAtSetpoint(intakeFunnel));
                        _coralController.RequestIntake(froggyCoralIntake, false);
                        _isIntaking = true;
                        SetWheelSpeeds(0, shooterAnimationWheelSpeeds);
                    }
                    else if (!hasCoral && (!hasAlgae || (hasAlgae && !shooterHasAlgae)) && (LastSetpoint == ReefscapeSetpoints.HighAlgae || LastSetpoint == ReefscapeSetpoints.LowAlgae || LastSetpoint == ReefscapeSetpoints.Stack))
                    {
                        frogState = FroggyState.Stow;
                        _algaeController.SetTargetState(shooterAlgaeStowState);
                        _algaeController.RequestIntake(shooterAlgaeIntake);
                        _algaeController.RequestIntake(froggyAlgaeIntake, false);
                        _isIntaking = true;
                        SetWheelSpeeds(0, -shooterAnimationWheelSpeeds);
                    }
                    else if (CurrentRobotMode == ReefscapeRobotMode.Algae && !hasAlgae)
                    {
                        frogState = FroggyState.AlgaeIntake;
                        RunFroggyWheels();
                        SetSetpoint(froggyLolli ? froggyLollipop : froggyAlgae);
                        _algaeController.SetTargetState(froggyAlgaeStowState);
                        _algaeController.RequestIntake(froggyAlgaeIntake);
                        _algaeController.RequestIntake(shooterAlgaeIntake, false);
                        _froggyWheels = 6000f;
                        _isIntaking = true;
                        
                        SetWheelSpeeds(-froggyAnimationWheelSpeeds, 0);
                    }
                    
                    break;
                case ReefscapeSetpoints.Place:
                    if (!stillInPlaceState)
                    {
                        if (shooterHasAlgae && LastSetpoint == ReefscapeSetpoints.Barge)
                        {
                            SetSetpoint(bargePlace);
                        }
                        else if (shooterHasCoral && LastSetpoint == ReefscapeSetpoints.L4)
                        {
                            SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL4 : backL4Scored);
                        }

                        PlacePiece();
                    }

                    break;
                case ReefscapeSetpoints.L1:
                    if (!shooterHasCoral) SetSetpoint(froggyCoralPlace);
                    else if ((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(eeL1);
                    frogState = FroggyState.Stow;

                    _algaeController.RequestIntake(funnelCoralIntake, false);
                    _coralController.RequestIntake(froggyCoralIntake, false);
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    foreach (var col in shooterCollidersForAlgae)
                    {
                        col.enabled = true;
                    }
                    break;
                case ReefscapeSetpoints.Stack:
                    if (!shooterHasCoral && !hasAlgae)
                    {
                        if (((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8)) SetSetpoint(lollipopIntake);
                        _algaeController.SetTargetState(shooterAlgaeStowState);
                        bool stackIntaking = IntakeAction.IsPressed() && !hasAlgae;
                        _algaeController.RequestIntake(shooterAlgaeIntake, stackIntaking);
                        _algaeController.RequestIntake(froggyAlgaeIntake, false);
                        if (stackIntaking)
                        {
                            _isIntaking = true;
                            SetWheelSpeeds(0, -shooterAnimationWheelSpeeds);
                        }
                        foreach (var col in shooterCollidersForAlgae)
                        {
                            col.enabled = true;
                        }
                    }

                    break;
                case ReefscapeSetpoints.L2:
                    frogState = FroggyState.Stow;
                    if (shooterHasCoral && ((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8))
                    {
                        SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL2 : backL2);
                    }
                    _algaeController.RequestIntake(funnelCoralIntake, false);
                    _coralController.RequestIntake(froggyCoralIntake, false);
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    foreach (var col in shooterCollidersForAlgae)
                    {
                        col.enabled = true;
                    }
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    frogState = FroggyState.Stow;
                    if (!shooterHasCoral && !hasAlgae)
                    {
                        if ((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(IsFacingReef(GetClosestReef()) ? frontLowAlgae : backLowAlgae);
                        _algaeController.SetTargetState(shooterAlgaeStowState);
                        bool lowAlgaeIntaking = IntakeAction.IsPressed() && !hasAlgae;
                        _algaeController.RequestIntake(shooterAlgaeIntake, lowAlgaeIntaking);
                        _algaeController.RequestIntake(froggyAlgaeIntake, false);
                        if (lowAlgaeIntaking)
                        {
                            _isIntaking = true;
                            SetWheelSpeeds(0, -shooterAnimationWheelSpeeds);
                        }
                        foreach (var col in shooterCollidersForAlgae)
                        {
                            col.enabled = true;
                        }
                    }

                    break;
                case ReefscapeSetpoints.L3:
                    frogState = FroggyState.Stow;
                    if (shooterHasCoral && ((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8))
                    {
                        SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL3 : backL3);
                    }
                    _algaeController.RequestIntake(funnelCoralIntake, false);
                    _coralController.RequestIntake(froggyCoralIntake, false);
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    frogState = FroggyState.Stow;
                    if (!shooterHasCoral && !hasAlgae)
                    {
                        if ((!SuperstructureAtSetpoint(backL4) && !SuperstructureAtSetpoint(frontL4)) || DistanceToReef(GetClosestReef()) > 1.8) SetSetpoint(IsFacingReef(GetClosestReef()) ? frontHighAlgae : backHighAlgae);
                        _algaeController.SetTargetState(shooterAlgaeStowState);
                        bool highAlgaeIntaking = IntakeAction.IsPressed() && !hasAlgae;
                        _algaeController.RequestIntake(shooterAlgaeIntake, highAlgaeIntaking);
                        _algaeController.RequestIntake(froggyAlgaeIntake, false);
                        if (highAlgaeIntaking)
                        {
                            _isIntaking = true;
                            SetWheelSpeeds(0, -shooterAnimationWheelSpeeds);
                        }
                        foreach (var col in shooterCollidersForAlgae)
                        {
                            col.enabled = true;
                        }
                    }

                    break;
                case ReefscapeSetpoints.L4:
                    frogState = FroggyState.Stow;
                    _algaeController.RequestIntake(funnelCoralIntake, false);
                    _coralController.RequestIntake(froggyCoralIntake, false);
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    if (shooterHasCoral)
                    {
                        SetSetpoint(IsFacingReef(GetClosestReef()) ? frontL4 : backL4);
                    }
                    foreach (var col in shooterCollidersForAlgae)
                    {
                        col.enabled = true;
                    }

                    AutoAlignnnn();
                    break;
                case ReefscapeSetpoints.Processor:
                    SetSetpoint(shooterHasAlgae ? process : froggyAlgaeProcess);
                    _algaeController.RequestIntake(shooterAlgaeIntake, false);
                    _coralController.RequestIntake(froggyCoralIntake, false);
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    break;
                case ReefscapeSetpoints.Barge:
                    frogState = FroggyState.Stow;
                    if (shooterHasAlgae)
                    {
                        SetSetpoint(bargePrep);
                    }
                    _algaeController.RequestIntake(shooterAlgaeIntake, false);
                    _coralController.RequestIntake(froggyCoralIntake, false);
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    froggyLolli = !froggyLolli;
                    break;
                case ReefscapeSetpoints.Climb:
                    frogState = FroggyState.Stow;
                    SetSetpoint(climbPrep);
                    _algaeController.RequestIntake(shooterAlgaeIntake, false);
                    _coralController.RequestIntake(froggyCoralIntake, false);
                    _coralController.RequestIntake(shooterAlgaeIntake, false);
                    _algaeController.RequestIntake(froggyAlgaeIntake, false);
                    break;
                case ReefscapeSetpoints.Climbed:
                    frogState = FroggyState.Stow;
                    SetSetpoint(climbClimb);
                    break;
            }
            
            UpdateSetpoints();
            AutoAlignnnn();
            RunFroggyWheels();
            // UpdateAudio();
        }
        
        private float DistanceToReef(Vector3 reefPos)
        {
            return Mathf.Sqrt(Mathf.Pow(transform.position.x - reefPos.x, 2) + Mathf.Pow(transform.position.z - reefPos.z, 2));
        }
    
        private Vector3 GetClosestReef()
        {
            return DistanceToReef(_blueReef) < DistanceToReef(_redReef) ? _blueReef : _redReef;
        }

        private bool IsFacingReef(Vector3 reefPos)
        {
            var toReefVector = (reefPos - transform.position).normalized;
            var robotForwardVector = transform.forward.normalized;
            var angle = Vector3.Dot(robotForwardVector, toReefVector);
            return angle > 0.0f;
        }
        private bool ElevatorAtSetpoint(StuyPulseSetpoint targetSetpoint)
        {
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), targetSetpoint.elevatorHeight, 2f);

            return elevatorAtSetpoint;
        }
        
        private bool IntakeAtSetpoint(StuyPulseSetpoint targetSetpoint)
        {
            bool intakeAtSetpoint = Utils.InAngularRange(eeArm.GetSingleAxisAngle(JointAxis.X), targetSetpoint.eeArmAngle, 2f);

            return intakeAtSetpoint;
        }

        private bool SuperstructureAtSetpoint(StuyPulseSetpoint targetSetpoint)
        {
            return IntakeAtSetpoint(targetSetpoint) && ElevatorAtSetpoint(targetSetpoint);
        }


    }

}