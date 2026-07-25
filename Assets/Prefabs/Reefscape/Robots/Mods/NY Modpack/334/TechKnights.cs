using System;
using Games.Reefscape.Enums;
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

namespace Prefabs.Reefscape.Robots.Mods.TechKnights._334
{
    public class TechKnights: ReefscapeRobotBase
    {
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint endEffectorJoint;
        [SerializeField] private GenericJoint intakeJoint;
        [SerializeField] private ReefscapeAutoAlign align;
        
        [Header("Align Offsets")]
        [SerializeField] private TechKnightsAlignOffset prepOffset;
        [SerializeField] private TechKnightsAlignOffset l4Offset, l3Offset, l2Offset, l1Offset;
        [SerializeField] private TechKnightsAlignOffset highAlgaeOffset, lowAlgaeOffset;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants endEffectorPid;
        [SerializeField] private PidConstants intakePid;
        
        [Header("Outtakes")]
        [SerializeField] private Vector3 coralL4OuttakeForce;
        [SerializeField] private Vector3 coralOuttakeForce;
        [SerializeField] private Vector3 algaeOuttake;

        [Header("Coral Setpoints")]
        [SerializeField] private TechKnightsSetpoint stow;
        [SerializeField] private TechKnightsSetpoint intake;
        [SerializeField] private TechKnightsSetpoint l1, l2, l3, l4;
        [SerializeField] private TechKnightsSetpoint lowAlgae, highAlgae, processor, intakeWithAlgae;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState algaeStowState;
        [SerializeField] private GamePieceState tspmoState, coralIntakeState, coralHandoffState, coralStowState;
        
        [Header("Algae Stall Audio")]
        [SerializeField] private AudioSource algaeStallSource;
        [SerializeField] private AudioClip algaeStallAudio;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource IntakeSource;
        [SerializeField] private AudioSource HandoffSource;
        [SerializeField] private AudioSource EESource;
        [SerializeField] private AudioClip rollerClip;
        
        [Header("Animation Rollers")]
        [SerializeField] private GenericAnimationJoint[] intakeRollers;
        [SerializeField] private GenericAnimationJoint[] eeRollers;
        [SerializeField] private GenericAnimationJoint[] handoffRollers;
        [SerializeField] private float intakeSpeed, eeSpeed, handoffSpeed;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;

        private float _elevatorTargetHeight;
        private float _endEffectorTargetAngle;
        private float _intakeTargetAngle;
        private LayerMask coralMask;
        private bool canClack;

        private bool lastSetpointL4;

        private bool placed = false;
        private bool handoff = false;

        private bool _intakeRollersActive;
        private bool _handoffRollersActive;
        private bool _eeRollersActive;
        
        protected override void Start()
        {
            base.Start();
            
            endEffectorJoint.SetPid(endEffectorPid);
            intakeJoint.SetPid(intakePid);

            _elevatorTargetHeight = 0;
            _endEffectorTargetAngle = 0;
            _intakeTargetAngle = 0;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Coral.ToString());
            _algaeController = RobotGamePieceController.GetPieceByName(ReefscapeGamePieceType.Algae.ToString());

            _coralController.gamePieceStates = new[]
            {
                tspmoState,
                coralIntakeState,
                coralHandoffState,
                coralStowState
            };
            _coralController.intakes.Add(coralIntake);

            _algaeController.gamePieceStates = new[]
            {
                algaeStowState
            };
            _algaeController.intakes.Add(algaeIntake);
            
            algaeStallSource.clip = algaeStallAudio;
            algaeStallSource.loop = true;
            algaeStallSource.Stop();
            
            IntakeSource.clip = rollerClip;
            IntakeSource.loop = true;
            IntakeSource.Stop();
            
            HandoffSource.clip = rollerClip;
            HandoffSource.loop = true;
            HandoffSource.Stop();
            
            EESource.clip = rollerClip;
            EESource.loop = true;
            EESource.Stop();

            coralMask = LayerMask.GetMask("Coral");
            canClack = true;

            lastSetpointL4 = false;
            placed = false;

            handoff = false;
        }

        private void LateUpdate()
        {
            endEffectorJoint.UpdatePid(endEffectorPid);
            intakeJoint.UpdatePid(intakePid);
        }

        private void FixedUpdate()
        {
            bool hasAlgae = _algaeController.HasPiece();
            bool hasCoral = _coralController.HasPiece();

            _intakeRollersActive = false;
            _handoffRollersActive = false;
            _eeRollersActive = false;

            _algaeController.SetTargetState(algaeStowState);
            _algaeController.RequestIntake(algaeIntake, !hasCoral && !hasAlgae && (CurrentSetpoint == ReefscapeSetpoints.LowAlgae || CurrentSetpoint == ReefscapeSetpoints.HighAlgae) && IntakeAction.IsPressed());

            AnimateCoral();
            PreventSetpoints();

            if (handoff)
            {
                RunRollers(eeRollers, eeSpeed, EESource);
                RunRollers(handoffRollers, handoffSpeed, HandoffSource);
                if (EndEffectorAtSetpoint(processor)) SetState(ReefscapeSetpoints.Stow);
            }
            
            var endEffectorHasCoral = _coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum;
            if (endEffectorHasCoral)
            {
                switch (CurrentSetpoint)
                {
                    case ReefscapeSetpoints.L1:
                        SetRobotMode(ReefscapeRobotMode.Coral);
                        SetSetpoint(l1);
                        break;
                    case ReefscapeSetpoints.L2:
                        SetRobotMode(ReefscapeRobotMode.Coral);
                        SetSetpoint(l2);
                        break;
                    case ReefscapeSetpoints.L3:
                        SetRobotMode(ReefscapeRobotMode.Coral);
                        SetSetpoint(l3);
                        break;
                    case ReefscapeSetpoints.L4:
                        SetRobotMode(ReefscapeRobotMode.Coral);
                        SetSetpoint(l4);
                        lastSetpointL4 = true;
                        break;
                }
            }
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    SetRobotMode(hasAlgae ? ReefscapeRobotMode.Algae : ReefscapeRobotMode.Coral);
                    SetSetpoint(hasAlgae ? processor : stow);
                    break;
                
                case ReefscapeSetpoints.Intake:
                    _coralController.RequestIntake(coralIntake, !_coralController.atTarget && Utils.InAngularRange(intakeJoint.GetSingleAxisAngle(JointAxis.X), -21f, 2f));
                    if (!(_coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum))
                    {
                        RunRollers(intakeRollers, intakeSpeed, IntakeSource);
                        RunRollers(handoffRollers, hasAlgae ? (_coralController.currentStateNum == coralHandoffState.stateNum && _coralController.atTarget) ? 0 : handoffSpeed : (EndEffectorAtSetpoint(intake)) ? handoffSpeed : 0f, HandoffSource);
                        RunRollers(eeRollers, hasAlgae ? 0 : eeSpeed, EESource);
                    }
                    SetSetpoint(hasAlgae ? intakeWithAlgae : intake);
                    break;
                
                case ReefscapeSetpoints.LowAlgae:
                    SetRobotMode(ReefscapeRobotMode.Algae);
                    SetSetpoint(lowAlgae);
                    if (!hasCoral && !hasAlgae && IntakeAction.IsPressed()) RunRollers(eeRollers, -eeSpeed, EESource);
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    SetRobotMode(ReefscapeRobotMode.Algae);
                    SetSetpoint(highAlgae);
                    if (!hasCoral && !hasAlgae && IntakeAction.IsPressed()) RunRollers(eeRollers, -eeSpeed, EESource);
                    break;
                
                case ReefscapeSetpoints.Processor:
                    SetRobotMode(ReefscapeRobotMode.Coral);
                    SetSetpoint(processor);
                    break;
                
                case ReefscapeSetpoints.Place:
                    PlacePiece();
                    break;
            }
            
            UpdateSetpoints();
            DealWithAutoAlign();
            UpdateAudio();

            if (OuttakeAction.IsPressed())
            {
                if (EndEffectorAtSetpoint(l4) || EndEffectorAtSetpoint(processor)) RunRollers(eeRollers, -eeSpeed, EESource);
                if (EndEffectorAtSetpoint(l3) || EndEffectorAtSetpoint(l2) || EndEffectorAtSetpoint(l1) || EndEffectorAtSetpoint(stow)) RunRollers(eeRollers, eeSpeed, EESource);
            }

            ResolveRollerAudio();

            if (CurrentSetpoint != ReefscapeSetpoints.Place) placed = false;
        }

        private void PlaySource(AudioSource source)
        {
            if (!source.isPlaying)
            {
                source.Play();
            }
        }

        private void StopSource(AudioSource source)
        {
            if (source.isPlaying)
            {
                source.Stop();
            }
        }

        private void AnimateCoral()
        {
            if (CoralAtState(tspmoState))
            {
                _coralController.SetTargetState(coralIntakeState);
            }
            else if (CoralAtState(coralIntakeState))
            {
                _coralController.SetTargetState(coralHandoffState);
            }
            else if (CoralAtState(coralHandoffState))
            {
                _coralController.SetTargetState(EndEffectorAtSetpoint(stow) && !_algaeController.HasPiece() ? coralStowState : coralHandoffState);
                if (!_algaeController.HasPiece()) handoff = true;
            }
            else if (CoralAtState(coralStowState))
            {
                _coralController.SetTargetState(coralStowState);
                handoff = false;
            }
            else
            {
                _coralController.SetTargetState(tspmoState);
            }
        }

        private void RunRollers(GenericAnimationJoint[] rollerGroup, float speed, AudioSource source)
        {
            foreach (var roller in rollerGroup)
            {
                roller.VelocityRoller(speed);
            }

            if (speed == 0f) return;
            if (source == IntakeSource) _intakeRollersActive = true;
            else if (source == HandoffSource) _handoffRollersActive = true;
            else if (source == EESource) _eeRollersActive = true;
        }

        private void ResolveRollerAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled) return;

            if (_intakeRollersActive) PlaySource(IntakeSource); else StopSource(IntakeSource);
            if (_handoffRollersActive) PlaySource(HandoffSource); else StopSource(HandoffSource);
            if (_eeRollersActive) PlaySource(EESource); else StopSource(EESource);
        }

        private void DealWithAutoAlign()
        {
            if (CurrentSetpoint == ReefscapeSetpoints.Place)
            {
                return;
            }

            var flip = false;
            if (GetActiveCamera().transform.eulerAngles.y < 180) flip = !flip;
            if (Math.Abs(transform.position.x) > 4.489323 && PlayerPrefs.GetInt("PerspectiveAutoAlign", 1) == 1) flip = !flip;
            if (transform.position.x > 0) flip = !flip;
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.L4:
                    WaitForElevator(l4, l4Offset, !flip);
                    break;
                case ReefscapeSetpoints.L3:
                    align.offset = !flip ? l3Offset.alignOffset : new Vector3(-l3Offset.alignOffset.x, l3Offset.alignOffset.y, l3Offset.alignOffset.z);
                    break;
                case ReefscapeSetpoints.L2:
                    align.offset = !flip ? l2Offset.alignOffset : new Vector3(-l2Offset.alignOffset.x, l2Offset.alignOffset.y, l2Offset.alignOffset.z);
                    break;
                case ReefscapeSetpoints.L1:
                    align.offset = !flip ? l1Offset.alignOffset : new Vector3(-l1Offset.alignOffset.x, l1Offset.alignOffset.y, l1Offset.alignOffset.z);
                    break;
                
                case ReefscapeSetpoints.HighAlgae:
                    WaitForElevator(highAlgae, highAlgaeOffset, !flip);
                    break;
                case ReefscapeSetpoints.LowAlgae:
                    WaitForElevator(lowAlgae, lowAlgaeOffset, !flip);
                    break;
                
                default:
                    align.offset = FlipAlignForSide(prepOffset, !flip);
                    break;
            }
        }

        private void WaitForElevator(TechKnightsSetpoint setpoint, TechKnightsAlignOffset offset, bool flip)
        {
            if (EndEffectorAtSetpoint(setpoint))
            {
                var a = flip ? offset.alignOffset : new Vector3(-offset.alignOffset.x, offset.alignOffset.y, offset.alignOffset.z);
                align.offset = (setpoint == lowAlgae || setpoint == highAlgae) ? FlipAlignForSide(offset, flip) : a;
            }
            else
            {
                align.offset = FlipAlignForSide(prepOffset, flip);
            }
        }

        private Vector3 FlipAlignForSide(TechKnightsAlignOffset offset, bool flip)
        {
            Vector3 a = offset.alignOffset;
            if (AutoAlignLeftAction.IsPressed())
            {
                return new Vector3(flip ? a.x : -a.x,  a.y, a.z);
            }
            return new Vector3(flip ? -a.x : a.x,  a.y, a.z);
        }

        private bool CoralAtState(GamePieceState state)
        {
            return _coralController.atTarget && _coralController.currentStateNum == state.stateNum;
        }

        private bool EndEffectorAtSetpoint(TechKnightsSetpoint setpoint)
        {
            return Utils.InRange(elevator.GetElevatorHeight(), setpoint.elevatorHeight, 1f) &&
                   Utils.InAngularRange(endEffectorJoint.GetSingleAxisAngle(JointAxis.X), setpoint.endEffectorAngle, 5);

        }

        private void PreventSetpoints()
        {
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Barge:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
                case ReefscapeSetpoints.RobotSpecial:
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }
        }

        private void PlacePiece()
        {
            if (placed) return;
            
            if (_algaeController.HasPiece())
            {
                _algaeController.ReleaseGamePieceWithForce(algaeOuttake);
                SetSetpoint(stow);
            }
            else
            {
                if (_coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum)
                {
                    _coralController.ReleaseGamePieceWithForce(LastSetpoint == ReefscapeSetpoints.L4
                        ? coralL4OuttakeForce      // L4 Release
                        : coralOuttakeForce);    // Other Release
                }
                else if (_coralController.atTarget && _coralController.currentStateNum == coralHandoffState.stateNum)
                {
                    _coralController.ReleaseGamePieceWithForce(new Vector3(0, 0, -4f));
                }
            }

            placed = true;
        }

        private void SetSetpoint(TechKnightsSetpoint setpoint)
        {
            if (setpoint == l4)
            {
                _elevatorTargetHeight = setpoint.elevatorHeight;
                _intakeTargetAngle = setpoint.intakeAngle;
                if (Utils.InRange(elevator.GetElevatorHeight(), setpoint.elevatorHeight, 5f))
                {
                    _endEffectorTargetAngle = setpoint.endEffectorAngle;
                }

                return;
            } 
            else if (lastSetpointL4)
            {
                _endEffectorTargetAngle = setpoint.endEffectorAngle;
                _intakeTargetAngle = setpoint.intakeAngle;
                if (Utils.InAngularRange(endEffectorJoint.GetSingleAxisAngle(JointAxis.X), setpoint.endEffectorAngle,
                        3))
                {
                    _elevatorTargetHeight = setpoint.elevatorHeight;
                    lastSetpointL4 = false;
                }

                return;
            }
            
            _elevatorTargetHeight = setpoint.elevatorHeight;
            _endEffectorTargetAngle = setpoint.endEffectorAngle;
            _intakeTargetAngle = setpoint.intakeAngle;
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            endEffectorJoint.SetTargetAngle(_endEffectorTargetAngle).withAxis(JointAxis.X);
            intakeJoint.SetTargetAngle(_intakeTargetAngle).withAxis(JointAxis.X);
        }

        private void UpdateAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (IntakeSource.isPlaying || HandoffSource.isPlaying || EESource.isPlaying || algaeStallSource.isPlaying)
                {
                    IntakeSource.Stop();
                    HandoffSource.Stop();
                    EESource.Stop();
                    algaeStallSource.Stop();
                }

                return;
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
    }
}