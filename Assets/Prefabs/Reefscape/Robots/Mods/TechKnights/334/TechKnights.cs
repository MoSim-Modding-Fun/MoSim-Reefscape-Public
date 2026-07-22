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
        
        [Header("PIDS")]
        [SerializeField] private PidConstants endEffectorPid;
        [SerializeField] private PidConstants intakePid;

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
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _algaeController;

        private float _elevatorTargetHeight;
        private float _endEffectorTargetAngle;
        private float _intakeTargetAngle;
        private LayerMask coralMask;
        private bool canClack;

        private bool lastSetpointL4;
        
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
            
            // _algaeController.gamePieceStates = new[] {algaeStowState};
            // _algaeController.intakes.Add(algaeIntake);
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
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow:
                    SetSetpoint(hasAlgae ? processor : stow);
                    break;
                
                case ReefscapeSetpoints.Intake:
                    if (!hasCoral) _coralController.SetTargetState(coralIntakeState);
                    if (CurrentRobotMode == ReefscapeRobotMode.Coral)
                    {
                        _coralController.RequestIntake(coralIntake, !hasCoral);
                    }
                    SetSetpoint(intake);
                    break;
                
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
            //UpdateAudio();
        }

        private void AnimateCoral()
        {
            if (CoralAtState(coralIntakeState))
            {
                _coralController.SetTargetState(coralHandoffState);
            }
            else if (CoralAtState(coralHandoffState))
            {
                _coralController.SetTargetState(EndEffectorAtSetpoint(intake) && !_algaeController.HasPiece() ? coralStowState : coralHandoffState);
            }
            else if (CoralAtState(coralStowState))
            {
                _coralController.SetTargetState(coralStowState);
            }
        }

        private bool CoralAtState(GamePieceState state)
        {
            return _coralController.atTarget && _coralController.currentStateNum == state.stateNum;
        }

        private bool EndEffectorAtSetpoint(TechKnightsSetpoint setpoint)
        {
            return Utils.InRange(elevator.GetElevatorHeight(), _elevatorTargetHeight, .2f) &&
                   Utils.InAngularRange(endEffectorJoint.GetSingleAxisAngle(JointAxis.X), _endEffectorTargetAngle, 5);

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
            if (_algaeController.HasPiece())
            {
                _algaeController.ReleaseGamePieceWithForce(new Vector3(0, 0, 0));
            }
            else
            {
                if (_coralController.atTarget && _coralController.currentStateNum == coralStowState.stateNum)
                {
                    _coralController.ReleaseGamePieceWithForce(LastSetpoint == ReefscapeSetpoints.L4
                        ? new Vector3(0, 0, 0)      // L4 Release
                        : new Vector3(0, 0, 0));    // Other Release
                }
            }
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
                if (Utils.InAngularRange(endEffectorJoint.GetSingleAxisAngle(JointAxis.X), setpoint.endEffectorAngle,
                        3))
                {
                    _elevatorTargetHeight = setpoint.elevatorHeight;
                    _intakeTargetAngle = setpoint.intakeAngle;
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
            endEffectorJoint.SetTargetAngle(_endEffectorTargetAngle).withAxis(JointAxis.X).useCustomStartingOffset(0);
            intakeJoint.SetTargetAngle(_intakeTargetAngle).withAxis(JointAxis.X).useCustomStartingOffset(0);
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