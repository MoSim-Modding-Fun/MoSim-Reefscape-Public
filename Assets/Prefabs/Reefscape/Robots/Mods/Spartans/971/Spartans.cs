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

namespace Prefabs.Reefscape.Robots.Mods.Spartans._971
{
    public class Spartans: ReefscapeRobotBase
    {
        #region Serialized Fields and Variables
        
        [Header("Components")]
        [SerializeField] private GenericElevator elevator;
            [SerializeField] private GenericJoint arm, wrist, intake, climber;
        
        [Header("PIDS")]
        [SerializeField] private PidConstants armPID;
            [SerializeField] private PidConstants wristPID, climberPID, intakePID;

        [Header("Setpoints")]
        [SerializeField] private SpartansSetpoint stow;
            [SerializeField] private SpartansSetpoint groundIntake, hpIntake, l1Front, l1Back, l2Front, l2Back, l3Front, l3Back, l4Front, l4Back;
            [SerializeField] private SpartansSetpoint groundAlgae, lowAlgaeFront, lowAlgaeBack, highAlgaeFront, highAlgaeBack, proc, bargeFront, bargeBack;
            [SerializeField] private SpartansSetpoint climbPrep, climbClimb;
        
        [Header("Intake Components")]
        [SerializeField] private ReefscapeGamePieceIntake algaeIntake;
            [SerializeField] private ReefscapeGamePieceIntake groundCoralIntake, hpCoralIntake;
        
        [Header("Game Piece States")]
        [SerializeField] private GamePieceState algaeStowState;
            [SerializeField] private GamePieceState groundIntakeState, endEffectorState;
        
        [Header("Robot Audio")]
        [SerializeField] private AudioSource rollerSource;
        [SerializeField] private AudioClip intakeClip;
        
        [Header("Funnel Close Audio")]
        [SerializeField] private AudioSource funnelCloseSource;
        [SerializeField] private AudioClip funnelCloseAudio;
        [SerializeField] private BoxCollider coralTrigger;
        private OverlapBoxBounds soundDetector;
        
        private RobotGamePieceController<ReefscapeGamePiece, ReefscapeGamePieceData>.GamePieceControllerNode _coralController, _algaeController;

        private float _elevatorTargetHeight, _armTargetAngle, _wristTargetAngle, _intakeTargetAngle, _climberTargetAngle;

        private LayerMask coralMask;
        private bool canClack;
        
        #endregion
        
        protected override void Start()
        {
            base.Start();
            
            arm.SetPid(armPID);
            wrist.SetPid(wristPID);
            intake.SetPid(intakePID);
            climber.SetPid(climberPID);

            _elevatorTargetHeight = stow.elevatorHeight;
            _armTargetAngle = stow.armAngle;
            _wristTargetAngle = stow.wristAngle;
            _climberTargetAngle = stow.climberAngle;
            _intakeTargetAngle = stow.intakeAngle;
            
            RobotGamePieceController.SetPreload(endEffectorState);
            _coralController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Coral));
            _algaeController = RobotGamePieceController.GetPieceByName(nameof(ReefscapeGamePieceType.Algae));

            _coralController.gamePieceStates = new[]
            {
                groundIntakeState,
                endEffectorState
            };
            _coralController.intakes.Add(groundCoralIntake);
            _coralController.intakes.Add(hpCoralIntake);

            _algaeController.gamePieceStates = new[]
            {
                algaeStowState
            };
            _algaeController.intakes.Add(algaeIntake);
            
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
            wrist.UpdatePid(wristPID);
            intake.UpdatePid(intakePID);
            climber.UpdatePid(climberPID);
        }

        private void FixedUpdate()
        {
            bool hasCoral = _coralController.HasPiece();
            
            switch (CurrentSetpoint)
            {
                case ReefscapeSetpoints.Stow: SetSetpoint(stow); break;
                case ReefscapeSetpoints.Intake: 
                    SetSetpoint(CurrentCoralStationMode.DropType == DropType.Ground ? groundIntake : hpIntake); 
                    
                    _coralController.SetTargetState(endEffectorState);
                    _coralController.RequestIntake(hpCoralIntake);
                    break;
                
                case ReefscapeSetpoints.Processor: SetState(ReefscapeSetpoints.Stow); break;
                case ReefscapeSetpoints.Stack: SetState(ReefscapeSetpoints.Stow); break;
                case ReefscapeSetpoints.Barge: SetState(ReefscapeSetpoints.Stow); break;
                
                case ReefscapeSetpoints.LowAlgae: SetSetpoint(stow); break;
                case ReefscapeSetpoints.HighAlgae: SetSetpoint(stow); break;
                
                case ReefscapeSetpoints.L1: if (_coralController.atTarget) SetSetpoint(l1Front); break;
                case ReefscapeSetpoints.L2: if (_coralController.atTarget) SetSetpoint(l2Front); break;
                case ReefscapeSetpoints.L3: if (_coralController.atTarget) SetSetpoint(l3Front); break;
                case ReefscapeSetpoints.L4: if (_coralController.atTarget) SetSetpoint(l4Front); break;
                
                case ReefscapeSetpoints.Climb: SetSetpoint(climbPrep); break;
                case ReefscapeSetpoints.Climbed: SetSetpoint(climbClimb); break;
                
                case ReefscapeSetpoints.Place: PlacePiece(); break;
                
                case ReefscapeSetpoints.RobotSpecial: 
                    CurrentCoralStationMode.DropType = (CurrentCoralStationMode.DropType == DropType.Ground ? DropType.Station : DropType.Ground);
                    CurrentCoralStationMode.DropOrientation = (CurrentCoralStationMode.DropOrientation == DropOrientation.Horizontal ? DropOrientation.Vertical : DropOrientation.Horizontal); 
                    SetState(ReefscapeSetpoints.Stow);
                    break;
            }
            
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

        private void UpdateSetpoints()
        {
            elevator.SetTarget(_elevatorTargetHeight);
            climber.SetTargetAngle(_climberTargetAngle).withAxis(JointAxis.Y)
                .useCustomStartingOffset(70);
            arm.SetTargetAngle(_armTargetAngle).withAxis(JointAxis.X)
                .noWrap(100)
                .useCustomStartingOffset(80);
            wrist.SetTargetAngle(_wristTargetAngle).withAxis(JointAxis.X);
            intake.SetTargetAngle(_intakeTargetAngle).withAxis(JointAxis.Y)
                .useCustomStartingOffset(70);
        }
        
        #endregion
        

        #region Logic Helpers
        
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
            if (LastSetpoint == ReefscapeSetpoints.L1)
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, -5.5f), 0.5f, 0.5f);
            }
            else
            {
                _coralController.ReleaseGamePieceWithContinuedForce(new Vector3(0, 0, 3), 0.5f, 0.5f);
            }
        }
        
        #endregion
    }
}