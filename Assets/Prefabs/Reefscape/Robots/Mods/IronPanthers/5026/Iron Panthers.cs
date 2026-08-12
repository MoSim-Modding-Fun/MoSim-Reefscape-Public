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

namespace Prefabs.Reefscape.Robots.Mods.IronPanthers._5026
{
    public class IronPanthers: ReefscapeRobotBase
    {
        #region Serialized Fields and Variables
        
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
        [SerializeField] private GenericJoint arm, climber, armSensor;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants armPID, climberPID, armSensorPID;

        [Header("Setpoints")]
        [SerializeField] private IronPanthersSetpoint stow, intake, l1, l2, l3, l4Prep, l4;
        [SerializeField] private IronPanthersSetpoint lowDescore, highDescore;
        [SerializeField] private float minimumElevatorHeightForSwingAround = 31;
        [SerializeField] private float climberStow, climberClimb;
        [SerializeField] private float armSensorL4 = 40;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake coralIntake;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState coralStowState;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource rollerSource;
        [SerializeField] private AudioClip intakeClip;
        
        [Header("Funnel Close Audio")]
        [SerializeField] private AudioSource funnelCloseSource;
        [SerializeField] private AudioClip funnelCloseAudio;
        [SerializeField] private BoxCollider coralTrigger;
        private OverlapBoxBounds soundDetector;
        
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController;

        private float _elevatorTargetHeight, _armTargetAngle, _armSensorTargetAngle, _climberTargetAngle;

        private LayerMask coralMask;
        private bool canClack;

        private bool _atSetpoint = true;

        #endregion
        
        protected override void Start()
        {
            base.Start();
            
            arm.SetPid(armPID);
            armSensor.SetPid(armSensorPID);
			climber.SetPid(climberPID);

            _elevatorTargetHeight = stow.elevatorHeight;
            _armTargetAngle = stow.armAngle;
            _climberTargetAngle = climberStow;
            _armSensorTargetAngle = 0;
            
            RobotGamePieceController.SetPreload(coralStowState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));

            _coralController.gamePieceStates = new[]
            {
                coralStowState
            };
            _coralController.intakes.Add(coralIntake);
            
            rollerSource.clip = intakeClip;
            rollerSource.loop = true;
            rollerSource.Stop();
            
            funnelCloseSource.clip = funnelCloseAudio;
            funnelCloseSource.loop = false;
            funnelCloseSource.Stop();

            soundDetector = new OverlapBoxBounds(coralTrigger);

            coralMask = LayerMask.GetMask("Coral");
            canClack = true;
        }

        private void LateUpdate()
        {
            arm.UpdatePid(armPID);
            climber.UpdatePid(climberPID);
        }

        private void FixedUpdate()
        {
            bool hasCoral = _coralController.HasPiece();
            
            _coralController.SetTargetState(coralStowState);
            _coralController.RequestIntake(coralIntake, SuperstructureAtSetpoint(intake) && IntakeAction.IsPressed() && !hasCoral);
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: SetSetpoint(stow); SetClimberAngle(climberStow); break;
                case ReefscapeSetpoints.Intake: SetSetpoint(intake); break;
                
                case ReefscapeSetpoints.Processor: SetState(ReefscapeSetpoints.Stow); break;
                case ReefscapeSetpoints.Stack: SetState(ReefscapeSetpoints.Stow); break;
                case ReefscapeSetpoints.Barge: SetState(ReefscapeSetpoints.Stow); break;
                
                case ReefscapeSetpoints.LowAlgae: SetSetpoint(lowDescore); break;
                case ReefscapeSetpoints.HighAlgae: SetSetpoint(highDescore); break;
                
                case ReefscapeSetpoints.L1: if (_coralController.atTarget) SetSetpoint(l1); break;
                case ReefscapeSetpoints.L2: if (_coralController.atTarget) SetSetpoint(l2); break;
                case ReefscapeSetpoints.L3: if (_coralController.atTarget) SetSetpoint(l3); break;
                case ReefscapeSetpoints.L4: if (_coralController.atTarget) SetSetpoint(l4); break;
                
                case ReefscapeSetpoints.Climb: SetSetpoint(stow); SetClimberAngle(climberClimb); break;
                case ReefscapeSetpoints.Climbed: SetClimberAngle(climberStow); break;
                
                case ReefscapeSetpoints.Place: PlacePiece(); break;
            }
            
            
            SetArmSensorAngle(CurrentSetpoint == ReefscapeSetpoints.L4 || (LastSetpoint == ReefscapeSetpoints.L4 && CurrentSetpoint == ReefscapeSetpoints.Place) ? armSensorL4 : 0);
            
            UpdateSetpoints();
            UpdateAudio();
        }

        #region Actuators & Setpoints

        private void SetSetpoint(IronPanthersSetpoint setpoint)
        {
            if (LastSetpoint != ReefscapeSetpoints.L4 && (CurrentSetpoint == ReefscapeSetpoints.L2 || CurrentSetpoint == ReefscapeSetpoints.L3))
            {
                _elevatorTargetHeight = setpoint.elevatorHeight;
                _armTargetAngle = ElevatorAtHeight(setpoint.elevatorHeight) ? setpoint.armAngle : 10;
                return;
            }

            if (ArmAtAngle(setpoint.armAngle))
            {
                _elevatorTargetHeight = setpoint.elevatorHeight;
                return;
            }

            if (ElevatorAtHeight(minimumElevatorHeightForSwingAround))
            {
                _armTargetAngle = setpoint.armAngle;
            }
            else
            {
                _elevatorTargetHeight = minimumElevatorHeightForSwingAround;
            }
        }

        private void SetClimberAngle(float climberAngle)
        {
            _climberTargetAngle = climberAngle;
        }

        private void SetArmSensorAngle(float armSensorAngle)
        {
            _armSensorTargetAngle = armSensorAngle;
        }

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.X);
            armSensor.SetTargetAngle(_armSensorTargetAngle).withAxis(JointAxis.X);
            arm.SetTargetAngle(_armTargetAngle).withAxis(JointAxis.X)
                .noWrap(270);
        }
        
        #endregion
        

        #region Logic Helpers

        private bool SuperstructureAtSetpoint(IronPanthersSetpoint targetSetpoint)
        {
            bool armAtSetpoint = Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), targetSetpoint.armAngle, 5f);
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), targetSetpoint.elevatorHeight, 5f);

            return armAtSetpoint && elevatorAtSetpoint;
        }
        
        private bool ElevatorAtHeight(float targetHeight)
        {
            bool elevatorAtSetpoint = Utils.InRange(elevator.GetElevatorHeight(), targetHeight, 5f);

            return elevatorAtSetpoint;
        }
        
        private bool ArmAtAngle(float targetAngle)
        {
            bool armAtSetpoint = Utils.InAngularRange(arm.GetSingleAxisAngle(JointAxis.X), targetAngle, 5f);

            return armAtSetpoint;
        }

        private void UpdateAudio()
        {
            if (BaseGameManager.Instance.RobotState == RobotState.Disabled)
            {
                if (rollerSource.isPlaying)
                {
                    rollerSource.Stop();
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
            else if (IntakeAction.IsPressed() && (_coralController.HasPiece()))
            {
                rollerSource.Stop();
            }

            var a = soundDetector.OverlapBox(coralMask);
            if (a.Length > 0)
            {
                if (canClack && !funnelCloseSource.isPlaying)
                {
                    funnelCloseSource.Play();
                    canClack = false;
                }
            }
            else
            {
                canClack = true;
            }
        }

        private void PlacePiece()
        {
            if (LastSetpoint == ReefscapeSetpoints.L4)
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, -5.5f), 0.5f, 0.5f);
            }
            else
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 3), 0.5f, 1f);
            }
        }
        
        #endregion
    }
}