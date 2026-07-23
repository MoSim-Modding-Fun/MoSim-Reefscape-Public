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
        [SerializeField] private TechKnightsSetpoint lowAlgae, highAlgae, processor;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState algaeStowState;
        [SerializeField] private GamePieceState coralIntakeState, coralHandoffState, coralStowState;
        
        [Header("Algae Stall Audio")]
        [SerializeField] private AudioSource algaeStallSource;
        [SerializeField] private AudioClip algaeStallAudio;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource rollerSource;
        [SerializeField] private AudioClip intakeClip;
        
        [Header("Animation Rollers")]
        [SerializeField] private GenericAnimationJoint[] intakeRollers;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;

        private float _elevatorTargetHeight;
        private float _endEffectorTargetAngle;
        private float _intakeTargetAngle;
        private LayerMask coralMask;
        private bool canClack;

        private bool lastSetpointL4;

        private bool placed = false;
        
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
            //
            // algaeStallSource.clip = algaeStallAudio;
            // algaeStallSource.loop = true;
            // algaeStallSource.Stop();
            //
            // rollerSource.clip = intakeClip;
            // rollerSource.loop = true;
            // rollerSource.Stop();

            coralMask = LayerMask.GetMask("Coral");
            canClack = true;

            lastSetpointL4 = false;
            placed = false;
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
            
            _algaeController.SetTargetState(algaeStowState);
            _algaeController.RequestIntake(algaeIntake, !hasCoral && !hasAlgae && (CurrentSetpoint == ReefscapeSetpoints.LowAlgae || CurrentSetpoint == ReefscapeSetpoints.HighAlgae) && IntakeAction.IsPressed());

            AnimateCoral();
            PreventSetpoints();
            
            var endEffectorHasCoral = _coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum;
            if (endEffectorHasCoral)
            {
                switch (CurrentSetpoint)
                {
                    case ReefscapeSetpoints.L1:
                        SetSetpoint(l1);
                        break;
                    case ReefscapeSetpoints.L2:
                        SetSetpoint(l2);
                        break;
                    case ReefscapeSetpoints.L3:
                        SetSetpoint(l3);
                        break;
                    case ReefscapeSetpoints.L4:
                        SetSetpoint(l4);
                        lastSetpointL4 = true;
                        break;
                }
            }
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    SetSetpoint(hasAlgae ? processor : stow);
                    break;
                
                case ReefscapeSetpoints.Intake:
                    if (CurrentRobotMode == ReefscapeRobotMode.Coral || !hasAlgae)
                    {
                        _coralController.RequestIntake(coralIntake, !_coralController.atTarget);
                        if (!(_coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum))
                        {
                            RunRollers(intakeRollers, -1000);
                        }
                    }
                    SetSetpoint(intake);
                    break;
                
                case ReefscapeSetpoints.LowAlgae:
                    SetSetpoint(lowAlgae);
                    break;
                case ReefscapeSetpoints.HighAlgae:
                    SetSetpoint(highAlgae);
                    break;
                
                case ReefscapeSetpoints.Processor:
                    SetSetpoint(processor);
                    break;
                
                case ReefscapeSetpoints.Place:
                    PlacePiece();
                    break;
            }
            
            UpdateSetpoints();
            DealWithAutoAlign();
            //UpdateAudio();
            
            if (CurrentSetpoint != ReefscapeSetpoints.Place) placed = false;
        }

        private void AnimateCoral()
        {
            if (CoralAtState(coralIntakeState))
            {
                _coralController.SetTargetState(coralHandoffState);
            }
            else if (CoralAtState(coralHandoffState))
            {
                print(EndEffectorAtSetpoint(intake));
                _coralController.SetTargetState(EndEffectorAtSetpoint(stow) && !_algaeController.HasPiece() ? coralStowState : coralHandoffState);
            }
            else if (CoralAtState(coralStowState))
            {
                _coralController.SetTargetState(coralStowState);
            }
            else
            {
                _coralController.SetTargetState(coralIntakeState);
            }
        }

        private void RunRollers(GenericAnimationJoint[] rollerGroup, float speed)
        {
            foreach (var roller in rollerGroup)
            {
                roller.VelocityRoller(speed);
            }
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
            return Utils.InRange(elevator.GetElevatorHeight(), setpoint.elevatorHeight, 2f) &&
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
                if (Utils.InRange(elevator.GetElevatorHeight(), setpoint.elevatorHeight, 2f))
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
                if (rollerSource.isPlaying || algaeStallSource.isPlaying)
                {
                    rollerSource.Stop();
                    algaeStallSource.Stop();
                }

                return;
            }

            if (((IntakeAction.IsPressed() && !_coralController.HasPiece() && !_coralController.HasPiece()) ||
                 OuttakeAction.IsPressed()) &&
                !rollerSource.isPlaying)
            {
                rollerSource.Play();
            }
            else if (!IntakeAction.IsPressed() && !OuttakeAction.IsPressed() && rollerSource.isPlaying)
            {
                rollerSource.Stop();
            }
            else if (IntakeAction.IsPressed() && (_coralController.HasPiece() || _algaeController.HasPiece()))
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
    }
}